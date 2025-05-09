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
    [Info("RustProtoGen", "SegFault", "1.0.4")]
    [Description("Generates a .proto file for Rust+ from reversing the Rust.Data.dll")]
    public class RustProtoGen : CSharpPlugin
    {
        private const string OutPath = "oxide/plugins/rustplus.proto";
        
        private readonly StringBuilder proto = new StringBuilder();
        private readonly HashSet<string> procTypes = new HashSet<string>();
        private readonly HashSet<string> typesToProc = new HashSet<string>();
        private Assembly rustAsm;
        private readonly Dictionary<Type, string> typeMap = new Dictionary<Type, string>{
            { typeof(int), "int32" },
            { typeof(long), "int64" },
            { typeof(uint), "uint32" },
            { typeof(ulong), "uint64" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(bool), "bool" },
            { typeof(string), "string" },
            { typeof(byte[]), "bytes" },
            { typeof(NetworkableId), "uint32" },
            { typeof(ArraySegment<byte>), "bytes" },
            { typeof(UnityEngine.Vector2), "Vector2" },
            { typeof(UnityEngine.Vector3), "Vector3" },
            { typeof(UnityEngine.Vector4), "Vector4" },
            { typeof(System.Byte), "bytes" },
        };

        void Init()
        {
            try
            {
                Puts("Starting protobuf generation...");
                GenProtoFile();
                Puts("Protobuf generation completed. Check " + OutPath);
            }
            catch (Exception ex)
            {
                Puts($"Error during protobuf generation: {ex.Message}");
                if (ex.InnerException != null)
                    Puts($"Inner exception: {ex.InnerException.Message}");
                if (ex.StackTrace != null)
                    Puts($"Stack trace: {ex.StackTrace}");
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
            
            AddVectorDefinitions();

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
                var type = rustAsm.GetType(typeName);
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
        
        void AddVectorDefinitions()
        {
            var vectors = new[]
            {
                new { Name = "Vector2", Fields = new[] { "x", "y" } },
                new { Name = "Vector3", Fields = new[] { "x", "y", "z" } },
                new { Name = "Vector4", Fields = new[] { "x", "y", "z", "w" } }
            };
            
            foreach (var vector in vectors)
            {
                proto.AppendLine($"message {vector.Name} {{");
                for (int i = 0; i < vector.Fields.Length; i++)
                {
                    proto.AppendLine($"\tfloat {vector.Fields[i]} = {i + 1};");
                }
                proto.AppendLine("}");
                proto.AppendLine();
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
            
            Puts($"Generating message: {type.Name}");
            procTypes.Add(type.FullName);
            string indentStr = new string('\t', indent);
            sb.AppendLine($"{indentStr}message {type.Name} {{");
            indent++;

            var members = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => new { Name = f.Name, Type = f.FieldType, IsField = true })
                .Union(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => new { Name = p.Name, Type = p.PropertyType, IsField = false }))
                .Where(m => !m.Name.Equals("ShouldPool", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var optionalFields = DetectOptionalFields(type);
            var (requiredFields, fieldNumbers) = ParseSerializeFunction(type);
            
            Puts($"Found {members.Count} members in {type.Name}");
            
            int fieldNum = 1;
            foreach (var mem in members)
            {
                var protoType = MapType(mem.Type, type);
                bool isOpt = optionalFields.Contains(mem.Name) && !requiredFields.Contains(mem.Name);
                string opt = isOpt ? "optional " : "";

                if (!fieldNumbers.ContainsKey(mem.Name))
                {
                    Puts($"Field {mem.Name} not found in fieldNumbers");
                    sb.AppendLine($"{indentStr}\t{opt}{protoType} {mem.Name} = ERROR;");
                }
                else
                {
                    sb.AppendLine($"{indentStr}\t{opt}{protoType} {mem.Name} = {fieldNumbers[mem.Name]};");
                }
            }

            var nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.GetInterfaces().Any(i => i.Name == "IProto" || i.Name == "IProto`1") || t.IsEnum)
                .ToList();

            if (nested.Any())
            {
                Puts($"Found {nested.Count} nested types in {type.Name}");
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
            
            Puts($"Generating enum: {type.Name}");
            procTypes.Add(type.FullName);
            string indentStr = new string('\t', indent);
            sb.AppendLine($"{indentStr}enum {type.Name} {{");
            indent++;

            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type).Cast<int>().ToArray();
            
            Puts($"Found {names.Length} values in enum {type.Name}");
            
            bool hasZeroValue = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == 0)
                {
                    hasZeroValue = true;
                    break;
                }
            }
            
            if (!hasZeroValue)
            {
                sb.AppendLine($"{indentStr}\tUndefined = 0;");
            }
            
            for (int i = 0; i < names.Length; i++)
            {
                sb.AppendLine($"{indentStr}\t{names[i]} = {values[i]};");
            }

            indent--;
            sb.AppendLine($"{indentStr}}}");
        }

        HashSet<string> DetectOptionalFields(Type type)
        {
            var optFields = new HashSet<string>();

            var resetMethod = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "ResetToPool" && m.GetParameters().Length == 1 && 
                                   m.GetParameters()[0].ParameterType == type);
            if (resetMethod == null)
                return optFields;

            try
            {
                var instrs = PatchProcessor.GetCurrentInstructions(resetMethod);
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .ToDictionary(f => f.Name, f => f);

                string currentField = null;

                for (int i = 0; i < instrs.Count; i++)
                {
                    var instr = instrs[i];

                    if (instr.opcode == System.Reflection.Emit.OpCodes.Ldarg_0 || 
                        instr.opcode == System.Reflection.Emit.OpCodes.Ldarg_1)
                    {
                        if (i + 1 < instrs.Count && 
                            instrs[i + 1].opcode == System.Reflection.Emit.OpCodes.Ldfld &&
                            instrs[i + 1].operand is FieldInfo fieldInfo && 
                            fields.ContainsKey(fieldInfo.Name))
                        {
                            currentField = fieldInfo.Name;
                        }
                    }

                    if (currentField != null && 
                        instr.opcode == System.Reflection.Emit.OpCodes.Stfld && 
                        instr.operand is FieldInfo storeField && 
                        storeField.Name == currentField)
                    {
                        if (i > 0 && instrs[i - 1].opcode == System.Reflection.Emit.OpCodes.Ldnull)
                        {
                            optFields.Add(currentField);
                        }
                        currentField = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Puts($"Error detecting optional fields in ResetToPool for {type.Name}: {ex.Message}");
            }

            optFields.Remove("ShouldPool");

            return optFields;
        }

        (HashSet<string>, Dictionary<string, int>) ParseSerializeFunction(Type type)
        {
            var requiredFields = new HashSet<string>();
            var fieldNumbers = new Dictionary<string, int>();
            var serializeMethods = new List<MethodInfo>();
            
            var instanceMethod = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Serialize" && 
                                m.GetParameters().Any(p => p.ParameterType.Name.Contains("BufferStream")));
            if (instanceMethod != null)
                serializeMethods.Add(instanceMethod);
            
            var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Serialize" && 
                        m.GetParameters().Length >= 2 &&
                        m.GetParameters()[0].ParameterType.Name.Contains("BufferStream") &&
                        m.GetParameters()[1].ParameterType == type)
                .ToList();
            if (staticMethods.Any())
                serializeMethods.AddRange(staticMethods);
                
            if (!serializeMethods.Any())
                return (requiredFields, fieldNumbers);
                
            foreach (var serMethod in serializeMethods)
            {
                try
                {
                    var instrs = PatchProcessor.GetCurrentInstructions(serMethod);
                    if (instrs == null || instrs.Count == 0)
                        continue;
                    
                    bool lookingForWriteByte = true;
                    bool lookingForFieldName = false;
                    int byteValue = 0;
                    string pendingFieldName = null;
                    
                    for (int i = 0; i < instrs.Count; i++)
                    {
                        var instr = instrs[i];
                        
                        if (instr.opcode == System.Reflection.Emit.OpCodes.Ldarg_1 || 
                            instr.opcode == System.Reflection.Emit.OpCodes.Ldarg_0)
                        {
                            if (i + 1 < instrs.Count && 
                                instrs[i + 1].opcode == System.Reflection.Emit.OpCodes.Ldfld && 
                                instrs[i + 1].operand is FieldInfo fieldInfo)
                            {
                                if (lookingForFieldName && byteValue != 0) {                          
                                    fieldNumbers[fieldInfo.Name] = byteValue;
                                    byteValue = 0;
                                    lookingForFieldName = false;
                                }
                                
                                pendingFieldName = fieldInfo.Name;
                                i++;
                            }
                        }
                        
                        if (instr.opcode == System.Reflection.Emit.OpCodes.Newobj && 
                            instr.operand is ConstructorInfo ctor && 
                            ctor.DeclaringType.Name == "ArgumentNullException")
                        {
                            string fieldName = null;
                            bool hasRequiredMessage = false;
                            
                            for (int j = i - 1; j >= Math.Max(0, i - 10); j--)
                            {
                                if (instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldstr)
                                {
                                    string stringValue = instrs[j].operand as string;
                                    if (stringValue != null)
                                    {
                                        if (stringValue == "Required by proto specification.")
                                        {
                                            hasRequiredMessage = true;
                                        }
                                        else if (fieldName == null && stringValue.Length > 0)
                                        {
                                            fieldName = stringValue;
                                        }
                                    }
                                }
                            }
                            
                            if (fieldName != null && hasRequiredMessage)
                            {
                                requiredFields.Add(fieldName);
                            }
                        }
                        
                        if (IsLoadConstantInstruction(instr, out int constValue))
                        {
                            if (i + 1 < instrs.Count && 
                                IsMethodCallInstruction(instrs[i + 1], "WriteByte"))
                            {
                                if (lookingForWriteByte) {
                                    byteValue = constValue >> 3;
                                    lookingForWriteByte = false;
                                    lookingForFieldName = true;
                                    
                                    if (pendingFieldName != null && !fieldNumbers.ContainsKey(pendingFieldName))
                                    {
                                        fieldNumbers[pendingFieldName] = byteValue;
                                        pendingFieldName = null;
                                    }
                                    
                                    for (int j = i + 2; j < Math.Min(instrs.Count, i + 10); j++)
                                    {
                                        if (instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldfld && 
                                            instrs[j].operand is FieldInfo nextField)
                                        {
                                            if (!fieldNumbers.ContainsKey(nextField.Name))
                                            {
                                                fieldNumbers[nextField.Name] = byteValue;
                                            }
                                            break;
                                        }
                                    }
                                    
                                    i++;
                                } else if (byteValue != 0) {
                                    byteValue *= constValue;
                                }
                            }
                        }

                        if (!lookingForWriteByte) {
                            if (instr.operand is MethodInfo protoMethod && 
                                (protoMethod.DeclaringType?.Name == "ProtocolParser" || 
                                 protoMethod.Name == "Serialize"))
                            {
                                string fieldName = ExtractFieldNameFromInstructions(instrs, i - 1, 5);
                                
                                if (fieldName == null && protoMethod.GetParameters().Length >= 2)
                                {
                                    bool foundStream = false;
                                    
                                    for (int j = i - 5; j < i; j++)
                                    {
                                        if (j < 0) continue;
                                        
                                        if (!foundStream && 
                                            (instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldloc || 
                                             instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldloc_0 ||
                                             instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldloc_1))
                                        {
                                            foundStream = true;
                                        }
                                        else if (foundStream && instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldfld &&
                                                 instrs[j].operand is FieldInfo argField)
                                        {
                                            fieldName = argField.Name;
                                            break;
                                        }
                                    }
                                }
                                
                                if (fieldName != null && byteValue != 0 && !fieldNumbers.ContainsKey(fieldName))
                                {
                                    fieldNumbers[fieldName] = byteValue;
                                }
                                
                                if (protoMethod.Name.StartsWith("Write") || 
                                    protoMethod.Name == "Serialize")
                                {
                                    lookingForWriteByte = true;
                                }
                            }
                            else if (i - 5 > 0 && !ContainsFieldAccess(instrs, i - 5, i))
                            {
                                lookingForWriteByte = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Puts($"Error analyzing {serMethod.Name} for type {type.Name}: {ex.Message}");
                }
            }
            
            return (requiredFields, fieldNumbers);
        }

        private bool IsLoadConstantInstruction(CodeInstruction instr, out int value)
        {
            value = 0;
            
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_0) { value = 0; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_1) { value = 1; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_2) { value = 2; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_3) { value = 3; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_4) { value = 4; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_5) { value = 5; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_6) { value = 6; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_7) { value = 7; return true; }
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_8) { value = 8; return true; }
            
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4_S && instr.operand is sbyte sb)
            {
                value = sb;
                return true;
            }
            
            if (instr.opcode == System.Reflection.Emit.OpCodes.Ldc_I4 && instr.operand is int i)
            {
                value = i;
                return true;
            }
            
            return false;
        }

        private bool IsMethodCallInstruction(CodeInstruction instr, string methodName)
        {
            return (instr.opcode == System.Reflection.Emit.OpCodes.Call || 
                    instr.opcode == System.Reflection.Emit.OpCodes.Callvirt) &&
                instr.operand is MethodInfo method && 
                method.Name == methodName;
        }

        private string ExtractFieldNameFromInstructions(List<CodeInstruction> instrs, int startIndex, int lookBack = 10)
        {
            for (int j = startIndex; j >= Math.Max(0, startIndex - lookBack); j--)
            {
                if (instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldfld && 
                    instrs[j].operand is FieldInfo accessedField)
                {
                    return accessedField.Name;
                }
            }
            return null;
        }

        private bool ContainsFieldAccess(List<CodeInstruction> instrs, int startIndex, int endIndex)
        {
            for (int j = startIndex; j <= endIndex && j < instrs.Count; j++)
            {
                if (instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldfld || 
                    instrs[j].opcode == System.Reflection.Emit.OpCodes.Ldflda)
                {
                    return true;
                }
            }
            return false;
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

            if (typeMap.TryGetValue(type, out string protoType))
            {
                return protoType;
            }

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

            return "Unknown:" + type.FullName;
        }
    }
}