using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Oxide.Core;
using Oxide.Core.Plugins;
using HarmonyLib;

namespace Oxide.Plugins
{
    [Info("RustProtoGen", "SegFault", "1.0.2")]
    [Description("Generates a .proto file for Rust+ from reversing the Rust.Data.dll")]
    public class RustProtoGen : CSharpPlugin
    {
        private const string OutPath = "oxide/plugins/rustplus.proto";
        
        private readonly StringBuilder proto = new StringBuilder();
        private readonly HashSet<string> procTypes = new HashSet<string>();
        private readonly HashSet<string> typesToProc = new HashSet<string>();
        private Assembly rustAsm;

        void Init()
        {
            try
            {
                GenProtoFile();
                Puts("Protobuf generation completed. Check " + OutPath);
            }
            catch (Exception ex)
            {
                Puts($"Error during protobuf generation: {ex.Message}");
                if (ex.InnerException != null)
                    Puts($"Inner exception: {ex.InnerException.Message}");
            }
        }
        
        void GenProtoFile()
        {
            rustAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name.Equals("Rust.Data", StringComparison.OrdinalIgnoreCase));
            if (rustAsm == null)
            {
                Puts("Rust.Data.dll not found among loaded assemblies");
                return;
            }

            proto.Clear();
            proto.AppendLine("syntax = \"proto3\";");
            proto.AppendLine("package rustplus;");
            proto.AppendLine();

            var appMsgType = rustAsm.GetTypes()
                .FirstOrDefault(t => t.Name == "AppMessage" && t.Namespace == "ProtoBuf");
            if (appMsgType == null)
            {
                Puts("AppMessage type not found in Rust.Data");
                return;
            }
            
            var appRequestType = rustAsm.GetTypes()
                .FirstOrDefault(t => t.Name == "AppRequest" && t.Namespace == "ProtoBuf");
            if (appRequestType == null)
            {
                Puts("AppRequest type not found in Rust.Data");
                return;
            }

            procTypes.Clear();
            typesToProc.Clear();
            
            CollectTypes(appMsgType);
            CollectTypes(appRequestType);

            var typeResults = new List<string>();

            if (typesToProc.Contains(appMsgType.FullName))
            {
                var sb = new StringBuilder();
                GenMsg(appMsgType, 0, sb);
                typeResults.Add(sb.ToString().TrimEnd());
                typesToProc.Remove(appMsgType.FullName);
            }
            
            if (typesToProc.Contains(appRequestType.FullName))
            {
                var sb = new StringBuilder();
                GenMsg(appRequestType, 0, sb);
                typeResults.Add(sb.ToString().TrimEnd());
                typesToProc.Remove(appRequestType.FullName);
            }

            foreach (var typeName in typesToProc.OrderBy(t => t))
            {
                var type = rustAsm.GetTypes()
                    .FirstOrDefault(t => t.FullName == typeName);
                if (type == null)
                {
                    Puts($"Warning: Type {typeName} not found");
                    continue;
                }

                var sb = new StringBuilder();
                if (type.DeclaringType == null && type.IsEnum)
                    GenEnum(type, 0, sb);
                else if (type.DeclaringType == null)
                    GenMsg(type, 0, sb);
                
                typeResults.Add(sb.ToString().TrimEnd());
            }

            string content = string.Join("\n\n", typeResults);
            content = System.Text.RegularExpressions.Regex.Replace(content, @"\}\n\n\n+", "}\n\n");
            string finalOutput = proto.ToString() + content;

            try
            {
                System.IO.File.WriteAllText(OutPath, finalOutput);
                Puts($"Proto file written to {OutPath}");
            }
            catch (Exception ex)
            {
                Puts($"Failed to write proto file: {ex.Message}");
            }
        }

        void CollectTypes(Type type)
        {
            if (type == null || typesToProc.Contains(type.FullName))
                return;

            typesToProc.Add(type.FullName);

            var nestedTypes = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.GetInterfaces().Any(i => i.Name == "IProto" || i.Name == "IProto`1") || t.IsEnum)
                .ToList();
            foreach (var nested in nestedTypes)
            {
                CollectTypes(nested);
            }

            var members = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => new { Name = f.Name, Type = f.FieldType })
                .Union(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => new { Name = p.Name, Type = p.PropertyType }))
                .Where(m => !m.Name.Equals("ShouldPool", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var member in members)
            {
                var memType = member.Type;
                if (memType.IsGenericType && memType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    memType = memType.GetGenericArguments()[0];
                }
                else if (memType.IsArray)
                {
                    memType = memType.GetElementType();
                }

                if (memType.Namespace == "ProtoBuf" ||
                    memType.GetInterfaces().Any(i => i.Name == "IProto" || i.Name == "IProto`1") ||
                    memType.IsEnum)
                {
                    CollectTypes(memType);
                }
            }
        }

        void GenMsg(Type type, int indent, StringBuilder sb)
        {
            if (procTypes.Contains(type.FullName))
                return;

            Puts($"{new string(' ', indent * 2)}Generating message: {type.Name}");
            
            procTypes.Add(type.FullName);
            string indentStr = new string(' ', indent * 2);
            sb.AppendLine($"{indentStr}message {type.Name} {{");
            indent++;

            var members = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => new { Name = f.Name, Type = f.FieldType, IsField = true })
                .Union(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => new { Name = p.Name, Type = p.PropertyType, IsField = false }))
                .Where(m => !m.Name.Equals("ShouldPool", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var optionalFields = DetectOptionalFields(type);
            
            int fieldNum = 1;
            foreach (var mem in members)
            {
                var protoType = MapType(mem.Type, type);
                bool isOpt = optionalFields.Contains(mem.Name);
                string opt = isOpt ? "optional " : "";
                sb.AppendLine($"{indentStr}  {opt}{protoType} {mem.Name} = {fieldNum};");
                fieldNum++;
            }

            var nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.GetInterfaces().Any(i => i.Name == "IProto" || i.Name == "IProto`1") || t.IsEnum)
                .ToList();

            if (nested.Any())
            {
                Puts($"{new string(' ', indent * 2)}Found {nested.Count} nested types in {type.Name}");
                
                sb.AppendLine();
                
                var nestedOrdered = nested.OrderBy(t => t.Name).ToList();
                for (int i = 0; i < nestedOrdered.Count; i++)
                {
                    var nest = nestedOrdered[i];
                    
                    if (nest.IsEnum)
                        GenEnum(nest, indent, sb);
                    else
                        GenMsg(nest, indent, sb);
                    
                    if (i < nestedOrdered.Count - 1)
                    {
                        sb.AppendLine();
                    }
                }
            }

            indent--;
            sb.AppendLine($"{indentStr}}}");
        }

        void GenEnum(Type type, int indent, StringBuilder sb)
        {
            if (procTypes.Contains(type.FullName))
                return;

            Puts($"{new string(' ', indent * 2)}Generating enum: {type.Name}");
            
            procTypes.Add(type.FullName);
            string indentStr = new string(' ', indent * 2);
            sb.AppendLine($"{indentStr}enum {type.Name} {{");
            indent++;

            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type).Cast<int>().ToArray();
            
            for (int i = 0; i < names.Length; i++)
            {
                sb.AppendLine($"{indentStr}  {names[i]} = {values[i]};");
            }

            indent--;
            sb.AppendLine($"{indentStr}}}");
        }

        HashSet<string> DetectOptionalFields(Type type)
        {
            var optFields = new HashSet<string>();

            var serMethod = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Serialize" && m.GetParameters().Any(p => p.ParameterType.Name.Contains("BufferStream")));
            if (serMethod == null)
            {
                return optFields;
            }

            try
            {
                var instrs = PatchProcessor.GetCurrentInstructions(serMethod);
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .ToDictionary(f => f.Name, f => f);

                string currentField = null;

                for (int i = 0; i < instrs.Count; i++)
                {
                    var instr = instrs[i];

                    if (instr.opcode == System.Reflection.Emit.OpCodes.Ldfld &&
                        instr.operand is FieldInfo fieldInfo && fields.ContainsKey(fieldInfo.Name))
                    {
                        currentField = fieldInfo.Name;
                    }

                    if (currentField != null && (
                        instr.opcode == System.Reflection.Emit.OpCodes.Brfalse ||
                        instr.opcode == System.Reflection.Emit.OpCodes.Brfalse_S ||
                        instr.opcode == System.Reflection.Emit.OpCodes.Brtrue ||
                        instr.opcode == System.Reflection.Emit.OpCodes.Beq_S))
                    {
                        optFields.Add(currentField);
                        currentField = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Puts($"Error detecting optional fields: {ex.Message}");
            }

            return optFields;
        }

        string MapType(Type type, Type parent)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elemType = type.GetGenericArguments()[0];
                return $"repeated {MapType(elemType, parent)}";
            }
            if (type.IsArray)
            {
                var elemType = type.GetElementType();
                return $"repeated {MapType(elemType, parent)}";
            }

            if (type == typeof(int)) return "int32";
            else if (type == typeof(long)) return "int64";
            else if (type == typeof(uint)) return "uint32";
            else if (type == typeof(ulong)) return "uint64";
            else if (type == typeof(float)) return "float";
            else if (type == typeof(double)) return "double";
            else if (type == typeof(bool)) return "bool";
            else if (type == typeof(string)) return "string";
            else if (type == typeof(byte[])) return "bytes";
            else if (type == typeof(ArraySegment<byte>)) return "bytes";

            if (type.Namespace == "ProtoBuf" ||
                type.GetInterfaces().Any(i => i.Name == "IProto" || i.Name == "IProto`1") ||
                type.IsEnum)
            {
                if (!procTypes.Contains(type.FullName) && !typesToProc.Contains(type.FullName))
                {
                    typesToProc.Add(type.FullName);
                }
                
                if (type.DeclaringType != null)
                {
                    var parentType = type.DeclaringType;
                    if (parentType == parent)
                    {
                        return $"{parentType.Name}.{type.Name}";
                    }
                    else
                    {
                        var scope = type;
                        var typeName = type.Name;
                        while (scope.DeclaringType != null)
                        {
                            scope = scope.DeclaringType;
                            typeName = $"{scope.Name}.{typeName}";
                        }
                        return typeName;
                    }
                }
                
                return type.Name;
            }

            return "string";
        }
    }
}