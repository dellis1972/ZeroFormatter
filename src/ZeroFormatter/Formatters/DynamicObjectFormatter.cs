﻿using System;
using System.Collections.Generic;
using System.Linq;
using ZeroFormatter.Internal;
using ZeroFormatter.Segments;

#if !UNITY
using System.Reflection;
using System.Reflection.Emit;
#endif

namespace ZeroFormatter.Formatters
{
    // Layout: [int byteSize][lastIndex][indexOffset:int...][t format...], byteSize == -1 is null.

#if !UNITY

    internal class EmittableMemberInfo
    {
        readonly PropertyInfo propertyInfo;
        readonly FieldInfo fieldInfo;

        public bool IsProperty { get { return propertyInfo != null; } }
        public bool IsField { get { return fieldInfo != null; } }

        public PropertyInfo PropertyInfoUnsafe
        {
            get
            {
                if (!IsProperty) throw new InvalidOperationException();
                return propertyInfo;
            }
        }

        public string Name
        {
            get
            {
                return (IsProperty) ? propertyInfo.Name : fieldInfo.Name;
            }
        }

        public Type MemberType
        {
            get
            {
                return (IsProperty) ? propertyInfo.PropertyType : fieldInfo.FieldType;
            }
        }

        public EmittableMemberInfo(PropertyInfo propertyInfo)
        {
            this.propertyInfo = propertyInfo;
        }

        public EmittableMemberInfo(FieldInfo fieldInfo)
        {
            this.fieldInfo = fieldInfo;
        }

        public void EmitLoadValue(ILGenerator il)
        {
            if (IsProperty)
            {
                il.Emit(OpCodes.Callvirt, propertyInfo.GetGetMethod());
            }
            else
            {
                il.Emit(OpCodes.Ldfld, fieldInfo);
            }
        }
    }

    internal static class DynamicObjectDescriptor
    {
        public static Tuple<int, EmittableMemberInfo>[] GetMembers(Type type, bool isClass)
        {
            if (isClass)
            {
                if (!type.GetTypeInfo().IsClass)
                {
                    throw new InvalidOperationException("Type must be class. " + type.Name);
                }
            }
            else
            {
                if (!type.GetTypeInfo().IsValueType)
                {
                    throw new InvalidOperationException("Type must be class. " + type.Name);
                }
            }

            if (type.GetTypeInfo().GetCustomAttributes(typeof(ZeroFormattableAttribute), true).FirstOrDefault() == null)
            {
                throw new InvalidOperationException("Type must mark ZeroFormattableAttribute. " + type.Name);
            }

            if (isClass)
            {
                if (!type.GetTypeInfo().GetConstructors().Any(x => x.GetParameters().Length == 0))
                {
                    throw new InvalidOperationException("Type must needs parameterless constructor. " + type.Name);
                }
            }

            var dict = new Dictionary<int, EmittableMemberInfo>();
            foreach (var item in type.GetTypeInfo().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (item.GetCustomAttributes(typeof(IgnoreFormatAttribute), true).Any()) continue;

                var index = item.GetCustomAttributes(typeof(IndexAttribute), true).Cast<IndexAttribute>().FirstOrDefault();
                if (index == null)
                {
                    throw new InvalidOperationException("Public property must mark IndexAttribute or IgnoreFormatAttribute. " + type.Name + "." + item.Name);
                }

                var getMethod = item.GetGetMethod(true);
                var setMethod = item.GetSetMethod(true);

                if (isClass)
                {
                    if (getMethod == null || setMethod == null || getMethod.IsPrivate || setMethod.IsPrivate)
                    {
                        throw new InvalidOperationException("Public property's accessor must needs both public/protected get and set." + type.Name + "." + item.Name);
                    }
                }

                if (isClass)
                {
                    if (!getMethod.IsVirtual || !setMethod.IsVirtual)
                    {
                        throw new InvalidOperationException("Public property's accessor must be virtual. " + type.Name + "." + item.Name);
                    }
                }

                if (dict.ContainsKey(index.Index))
                {
                    throw new InvalidOperationException("IndexAttribute can not allow duplicate. " + type.Name + "." + item.Name + ", Index:" + index.Index);
                }

                var info = new EmittableMemberInfo(item);
                VerifyMember(info);
                dict[index.Index] = info;
            }

            foreach (var item in type.GetTypeInfo().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (item.GetCustomAttributes(typeof(IgnoreFormatAttribute), true).Any()) continue;

                var index = item.GetCustomAttributes(typeof(IndexAttribute), true).Cast<IndexAttribute>().FirstOrDefault();
                if (index == null)
                {
                    throw new InvalidOperationException("Public field must mark IndexAttribute or IgnoreFormatAttribute. " + type.Name + "." + item.Name);
                }
                else if (isClass)
                {
                    throw new InvalidOperationException("Class does not allow that field marks IndexAttribute. " + type.Name + "." + item.Name);
                }

                if (dict.ContainsKey(index.Index))
                {
                    throw new InvalidOperationException("IndexAttribute can not allow duplicate. " + type.Name + "." + item.Name + ", Index:" + index.Index);
                }

                var info = new EmittableMemberInfo(item);
                VerifyMember(info);
                dict[index.Index] = info;
            }

            return dict.OrderBy(x => x.Key).Select(x => Tuple.Create(x.Key, x.Value)).ToArray();
        }

        static void VerifyMember(EmittableMemberInfo info)
        {
            var formatter = typeof(Formatter<>).MakeGenericType(info.MemberType).GetTypeInfo().GetProperty("Default").GetValue(null, null);
            var error = formatter as IErrorFormatter;
            if (error != null)
            {
                throw error.GetException();
            }

            // OK, Can Serialzie.
        }
    }

    internal static class DynamicObjectFormatter
    {
        public static object Create<T>()
        {
            var t = typeof(T);
            var ti = t.GetTypeInfo();
            if (ti.IsValueType) throw new InvalidOperationException("Type must be Class. " + t.Name);

            var members = DynamicObjectDescriptor.GetMembers(t, true);
            var generateTypeInfo = BuildFormatter(ZeroFormatter.Segments.DynamicAssemblyHolder.Module, t, members);
            var formatter = Activator.CreateInstance(generateTypeInfo.AsType());

            return formatter;
        }

        static TypeInfo BuildFormatter(ModuleBuilder builder, Type elementType, Tuple<int, EmittableMemberInfo>[] memberInfos)
        {
            var typeBuilder = builder.DefineType(
               DynamicAssemblyHolder.ModuleName + "." + elementType.FullName + "$Formatter",
               TypeAttributes.Public,
               typeof(Formatter<>).MakeGenericType(elementType));

            // field
            var formattersInField = new List<Tuple<int, EmittableMemberInfo, FieldBuilder>>();
            foreach (var item in memberInfos)
            {
                var field = typeBuilder.DefineField("<>" + item.Item2.Name + "Formatter", typeof(Formatter<>).MakeGenericType(item.Item2.MemberType), FieldAttributes.Private | FieldAttributes.InitOnly);
                formattersInField.Add(Tuple.Create(item.Item1, item.Item2, field));
            }

            // .ctor
            {
                var method = typeBuilder.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, CallingConventions.Standard, Type.EmptyTypes);

                var il = method.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(Formatter<>).MakeGenericType(elementType).GetTypeInfo().GetConstructor(Type.EmptyTypes));

                foreach (var item in formattersInField)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, item.Item3.FieldType.GetTypeInfo().GetProperty("Default").GetGetMethod());
                    il.Emit(OpCodes.Stfld, item.Item3);
                }
                il.Emit(OpCodes.Ret);
            }

            // public override int? GetLength()
            {
                var method = typeBuilder.DefineMethod("GetLength", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    typeof(int?),
                    Type.EmptyTypes);

                var il = method.GetILGenerator();

                il.DeclareLocal(typeof(int?));

                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Initobj, typeof(int?));
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ret);
            }
            // public override int Serialize(ref byte[] bytes, int offset, T value)
            {
                var method = typeBuilder.DefineMethod("Serialize", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    typeof(int),
                    new Type[] { typeof(byte[]).MakeByRefType(), typeof(int), elementType });

                var il = method.GetILGenerator();

                var labelA = il.DefineLabel();
                var labelB = il.DefineLabel();
                il.DeclareLocal(typeof(IZeroFormatterSegment));
                il.DeclareLocal(typeof(int));

                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Isinst, typeof(IZeroFormatterSegment));
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Brfalse, labelA);

                // if(segment != null)
                {
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, typeof(IZeroFormatterSegment).GetTypeInfo().GetMethod("Serialize"));
                    il.Emit(OpCodes.Ret);
                }
                // else if(value == null)
                {
                    il.MarkLabel(labelA);
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Brtrue, labelB);

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4_M1);
                    il.Emit(OpCodes.Call, typeof(BinaryUtil).GetTypeInfo().GetMethod("WriteInt32"));
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldc_I4_4);
                    il.Emit(OpCodes.Ret);
                }
                // else
                {
                    il.MarkLabel(labelB);

                    var schemaLastIndex = memberInfos.Select(x => x.Item1).LastOrDefault();
                    var calcedBeginOffset = 8 + (4 * (schemaLastIndex + 1));

                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Stloc_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, calcedBeginOffset);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Starg_S, (byte)2);
                    for (int i = 0; i < formattersInField.Count; i++)
                    {
                        var item = formattersInField[i];

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloc_1);
                        il.Emit(OpCodes.Ldc_I4, 8 + (4 * item.Item1)); // 8 + 4 * index
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Ldarg_2);
                        if (i == 0)
                        {
                            il.Emit(OpCodes.Call, typeof(BinaryUtil).GetTypeInfo().GetMethod("WriteInt32"));
                            il.Emit(OpCodes.Pop);
                        }
                        else
                        {
                            il.Emit(OpCodes.Call, typeof(BinaryUtil).GetTypeInfo().GetMethod("WriteInt32Unsafe"));
                        }
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, item.Item3);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldarg_3);
                        item.Item2.EmitLoadValue(il);
                        il.Emit(OpCodes.Callvirt, item.Item3.FieldType.GetTypeInfo().GetMethod("Serialize"));
                        il.Emit(OpCodes.Add);
                        il.Emit(OpCodes.Starg_S, (byte)2);
                    }

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, schemaLastIndex);
                    il.Emit(OpCodes.Call, typeof(ObjectSegmentHelper).GetTypeInfo().GetMethod("WriteSize"));
                    il.Emit(OpCodes.Ret);
                }
            }
            // public override T Deserialize(ref byte[] bytes, int offset, DirtyTracker tracker, out int byteSize)
            {
                var method = typeBuilder.DefineMethod("Deserialize", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    elementType,
                    new[] { typeof(byte[]).MakeByRefType(), typeof(int), typeof(DirtyTracker), typeof(int).MakeByRefType() });

                var il = method.GetILGenerator();

                il.DeclareLocal(typeof(ArraySegment<byte>));
                var labelA = il.DefineLabel();

                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, typeof(BinaryUtil).GetTypeInfo().GetMethod("ReadInt32"));
                il.Emit(OpCodes.Stind_I4);

                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Ldc_I4_M1);
                il.Emit(OpCodes.Bne_Un_S, labelA);

                // if...
                {
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    il.Emit(OpCodes.Ldc_I4_4);
                    il.Emit(OpCodes.Stind_I4);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                }
                // else...
                {
                    il.MarkLabel(labelA);

                    il.Emit(OpCodes.Ldloca_S, (byte)0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldind_Ref);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    il.Emit(OpCodes.Ldind_I4);
                    il.Emit(OpCodes.Call, typeof(ArraySegment<byte>).GetTypeInfo().GetConstructor(new[] { typeof(byte[]), typeof(int), typeof(int) }));

                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Ldloc_0);
                    var ti = typeof(DynamicObjectSegmentBuilder<>).MakeGenericType(elementType).GetTypeInfo().GetMethod("GetProxyType").Invoke(null, null) as TypeInfo;
                    il.Emit(OpCodes.Newobj, ti.GetConstructor(new[] { typeof(DirtyTracker), typeof(ArraySegment<byte>) }));
                    il.Emit(OpCodes.Ret);
                }
            }

            return typeBuilder.CreateTypeInfo();
        }
    }

    internal static class DynamicStructFormatter
    {
        // create dynamic struct Formatter<T>
        public static object Create<T>()
        {
            var t = typeof(T);
            var ti = t.GetTypeInfo();
            if (!ti.IsValueType) throw new InvalidOperationException("Type must be ValueType. " + t.Name);

            if (ti.IsNullable())
            {
                var elementType = ti.GetGenericArguments()[0];
                var formatter = typeof(Formatter<>).MakeGenericType(elementType).GetTypeInfo().GetProperty("Default").GetGetMethod().Invoke(null, null);
                return Activator.CreateInstance(typeof(NullableStructFormatter<>).MakeGenericType(elementType), new[] { formatter });
            }
            else
            {
                var elementType = t;

                var members = DynamicObjectDescriptor.GetMembers(elementType, false);
                var length = ValidateAndCalculateLength(elementType, members);
                var generateTypeInfo = BuildFormatter(ZeroFormatter.Segments.DynamicAssemblyHolder.Module, elementType, length, members.Select(x => x.Item2).ToArray());
                var formatter = Activator.CreateInstance(generateTypeInfo.AsType());

                return formatter;
            }
        }

        static int? ValidateAndCalculateLength(Type t, IEnumerable<Tuple<int, EmittableMemberInfo>> source)
        {
            var isNullable = false;
            var lengthSum = 0;

            var constructorTypes = new List<Type>();
            var expected = 0;
            foreach (var item in source)
            {
                if (item.Item1 != expected) throw new InvalidOperationException("Struct index must be started with 0 and be sequential. Type: " + t.FullName + " InvalidIndex:" + item.Item1);
                expected++;

                var formatter = typeof(Formatter<>).MakeGenericType(item.Item2.MemberType).GetTypeInfo().GetProperty("Default");
                var len = (formatter.GetGetMethod().Invoke(null, Type.EmptyTypes) as IFormatter).GetLength();
                if (len != null)
                {
                    lengthSum += len.Value;
                }
                else
                {
                    isNullable = true;
                }

                constructorTypes.Add(item.Item2.MemberType);
            }

            var info = t.GetTypeInfo().GetConstructor(constructorTypes.ToArray());
            if (info == null)
            {
                throw new InvalidOperationException("Struct needs full parameter constructor of index property types. Type:" + t.FullName);
            }

            return isNullable ? (int?)null : lengthSum;
        }

        static TypeInfo BuildFormatter(ModuleBuilder builder, Type elementType, int? length, EmittableMemberInfo[] memberInfos)
        {
            var typeBuilder = builder.DefineType(
                DynamicAssemblyHolder.ModuleName + "." + elementType.FullName + "$Formatter",
                TypeAttributes.Public,
                typeof(Formatter<>).MakeGenericType(elementType));

            // public override int? GetLength()
            {
                var method = typeBuilder.DefineMethod("GetLength", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    typeof(int?),
                    Type.EmptyTypes);

                var il = method.GetILGenerator();
                if (length == null)
                {
                    il.DeclareLocal(typeof(int?));

                    il.Emit(OpCodes.Ldloca_S, (byte)0);
                    il.Emit(OpCodes.Initobj, typeof(int?));
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4, length.Value);
                    il.Emit(OpCodes.Newobj, typeof(int?).GetTypeInfo().GetConstructor(new[] { typeof(int) }));
                    il.Emit(OpCodes.Ret);
                }
            }
            // public override int Serialize(ref byte[] bytes, int offset, T value)
            {
                var method = typeBuilder.DefineMethod("Serialize", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    typeof(int),
                    new Type[] { typeof(byte[]).MakeByRefType(), typeof(int), elementType });

                var il = method.GetILGenerator();

                il.DeclareLocal(typeof(int)); // startOffset

                if (length != null)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, length.Value);
                    il.Emit(OpCodes.Call, typeof(BinaryUtil).GetTypeInfo().GetMethod("EnsureCapacity"));
                }

                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Stloc_0);

                foreach (var item in memberInfos)
                {
                    var formatter = typeof(Formatter<>).MakeGenericType(item.MemberType);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, formatter.GetTypeInfo().GetProperty("Default").GetGetMethod());
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldarga_S, (byte)3);
                    item.EmitLoadValue(il);
                    il.Emit(OpCodes.Callvirt, formatter.GetTypeInfo().GetMethod("Serialize"));
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Starg_S, (byte)2);
                }

                // offset - startOffset;
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Ret);
            }
            //// public override T Deserialize(ref byte[] bytes, int offset, DirtyTracker tracker, out int byteSize)
            {
                var method = typeBuilder.DefineMethod("Deserialize", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                    elementType,
                    new[] { typeof(byte[]).MakeByRefType(), typeof(int), typeof(DirtyTracker), typeof(int).MakeByRefType() });

                var il = method.GetILGenerator();

                il.DeclareLocal(typeof(int)); // size
                foreach (var item in memberInfos)
                {
                    il.DeclareLocal(item.MemberType); // item1, item2...
                }
                il.DeclareLocal(elementType); // result

                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stind_I4);
                for (int i = 0; i < memberInfos.Length; i++)
                {
                    var item = memberInfos[i];

                    var formatter = typeof(Formatter<>).MakeGenericType(item.MemberType);
                    il.Emit(OpCodes.Call, formatter.GetTypeInfo().GetProperty("Default").GetGetMethod());
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Ldloca_S, (byte)0);
                    il.Emit(OpCodes.Callvirt, formatter.GetTypeInfo().GetMethod("Deserialize"));
                    il.Emit(OpCodes.Stloc, i + 1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Starg_S, (byte)2);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldind_I4);
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stind_I4);
                }

                for (int i = 0; i < memberInfos.Length; i++)
                {
                    il.Emit(OpCodes.Ldloc, i + 1);
                }

                var constructor = elementType.GetTypeInfo().GetConstructor(memberInfos.Select(x => x.MemberType).ToArray());
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ret);
            }

            return typeBuilder.CreateTypeInfo();
        }
    }

#endif

    public class NullableStructFormatter<T> : Formatter<T?>
        where T : struct
    {
        readonly Formatter<T> innerFormatter;

        public NullableStructFormatter(Formatter<T> innerFormatter)
        {
            this.innerFormatter = innerFormatter;
        }

        public override int? GetLength()
        {
            var len = innerFormatter.GetLength();
            return (len == null) ? null : len + 1;
        }

        public override int Serialize(ref byte[] bytes, int offset, T? value)
        {
            var len = GetLength();
            if (len != null)
            {
                BinaryUtil.EnsureCapacity(ref bytes, offset, len.Value);
            }

            BinaryUtil.WriteBoolean(ref bytes, offset, value.HasValue);
            if (value.HasValue)
            {
                var startOffset = offset;
                offset += 1;
                offset += innerFormatter.Serialize(ref bytes, offset, value.Value);
                return offset - startOffset;
            }
            else
            {
                return 1;
            }
        }

        public override T? Deserialize(ref byte[] bytes, int offset, DirtyTracker tracker, out int byteSize)
        {
            byteSize = 1;
            var hasValue = BinaryUtil.ReadBoolean(ref bytes, offset);
            if (!hasValue) return null;

            offset += 1;

            int size;
            var v = innerFormatter.Deserialize(ref bytes, offset, tracker, out size);
            byteSize += size;

            return new Nullable<T>(v);
        }
    }
}

