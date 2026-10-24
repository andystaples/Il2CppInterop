using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;
using Object = Il2CppSystem.Object;

namespace Il2CppInterop.Runtime;

public static class DelegateSupport
{
    private static readonly ConcurrentDictionary<MethodSignature, Type> ourDelegateTypes = new();

    private static readonly AssemblyBuilder AssemblyBuilder =
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Il2CppTrampolineDelegates"), AssemblyBuilderAccess.Run);

    private static readonly ModuleBuilder ModuleBuilder =
        AssemblyBuilder.DefineDynamicModule("Il2CppTrampolineDelegates");

    private static readonly ConcurrentDictionary<MethodInfo, Delegate> NativeToManagedTrampolines = new();

    // One il2cpp delegate per managed delegate and target type, so removing from an il2cpp event finds what was added
    private static readonly ConditionalWeakTable<Delegate, ConcurrentDictionary<Type, Il2CppObjectBase>> ConvertedDelegates = new();

    // Two single delegates over the same target and method are equal in C#, `x -= new Action(Handler)` relies on that
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(MethodInfo, Type), Il2CppObjectBase>> ConvertedByTarget = new();
    private static readonly ConcurrentDictionary<(MethodInfo, Type), Il2CppObjectBase> ConvertedStatic = new();

    // Equal hashes do not make equal signatures, so the hash alone cannot name the type
    private static int ourDelegateTypeCount;

    internal static Type GetOrCreateDelegateType(MethodSignature signature, MethodInfo managedMethod)
    {
        return ourDelegateTypes.GetOrAdd(signature,
            (signature, managedMethodInner) =>
                CreateDelegateType(managedMethodInner, signature),
            managedMethod);
    }

    private static Type CreateDelegateType(MethodInfo managedMethodInner, MethodSignature signature)
    {
        var typeName = "Il2CppToManagedDelegate_" + managedMethodInner.DeclaringType + "_" + signature.GetHashCode() + "_" +
                       Interlocked.Increment(ref ourDelegateTypeCount) +
                       (signature.HasThis ? "HasThis" : "") +
                       (signature.HasReturnBuffer ? "ReturnBuffer" : "") +
                       (signature.ConstructedFromNative ? "FromNative" : "");

        var newType = ModuleBuilder.DefineType(typeName, TypeAttributes.Sealed | TypeAttributes.Public,
            typeof(MulticastDelegate));
        newType.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) })!,
            new object[] { CallingConvention.Cdecl }));

        var ctor = newType.DefineConstructor(
            MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName |
            MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof(object), typeof(IntPtr) });
        ctor.SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

        var parameterOffset = (signature.HasThis ? 1 : 0) + (signature.HasReturnBuffer ? 1 : 0);
        var managedParameters = managedMethodInner.GetParameters();
        var parameterTypes = new Type[managedParameters.Length + 1 + parameterOffset];
        if (signature.HasReturnBuffer)
            parameterTypes[0] = typeof(IntPtr);
        if (signature.HasThis)
            parameterTypes[signature.HasReturnBuffer ? 1 : 0] = typeof(IntPtr);
        parameterTypes[parameterTypes.Length - 1] = typeof(Il2CppMethodInfo*);
        for (var i = 0; i < managedParameters.Length; i++)
            parameterTypes[i + parameterOffset] = managedParameters[i].ParameterType.NativeType();
        var nativeReturnType = signature.HasReturnBuffer ? typeof(IntPtr) : managedMethodInner.ReturnType.NativeType();

        newType.DefineMethod("Invoke",
            MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Public,
            CallingConventions.HasThis,
            nativeReturnType,
            parameterTypes).SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

        newType.DefineMethod("BeginInvoke",
                MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot |
                MethodAttributes.Public,
                CallingConventions.HasThis, typeof(IAsyncResult),
                parameterTypes.Concat(new[] { typeof(AsyncCallback), typeof(object) }).ToArray())
            .SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

        newType.DefineMethod("EndInvoke",
            MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Public,
            CallingConventions.HasThis,
            nativeReturnType,
            new[] { typeof(IAsyncResult) }).SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

        return newType.CreateType();
    }

    private static string ExtractSignature(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.Append(methodInfo.ReturnType.FullName);
        if (!methodInfo.IsStatic)
        {
            builder.Append('_');
            builder.Append(methodInfo.DeclaringType!.FullName);
        }
        foreach (var parameterInfo in methodInfo.GetParameters())
        {
            builder.Append('_');
            builder.Append(parameterInfo.ParameterType.FullName);
        }

        return builder.ToString();
    }

    private static Delegate GetOrCreateNativeToManagedTrampoline(MethodSignature signature,
        Il2CppSystem.Reflection.MethodInfo nativeMethod, MethodInfo managedMethod)
    {
        return NativeToManagedTrampolines.GetOrAdd(managedMethod,
            (_, tuple) => GenerateNativeToManagedTrampoline(tuple.nativeMethod, tuple.managedMethod, tuple.signature),
            (nativeMethod, managedMethod, signature));
    }

    private static Delegate GenerateNativeToManagedTrampoline(Il2CppSystem.Reflection.MethodInfo nativeMethod,
        MethodInfo managedMethod, MethodSignature signature)
    {
        var returnType = managedMethod.ReturnType.NativeType();

        var managedParameters = managedMethod.GetParameters();
        var nativeParameters = nativeMethod.GetParameters();
        var parameterTypes = new Type[managedParameters.Length + 1 + 1]; // thisptr for target, methodInfo last arg
        parameterTypes[0] = typeof(IntPtr);
        parameterTypes[managedParameters.Length + 1] = typeof(Il2CppMethodInfo*);
        for (var i = 0; i < managedParameters.Length; i++)
            parameterTypes[i + 1] = managedParameters[i].ParameterType.NativeType();

        var trampoline = new DynamicMethod("(il2cpp delegate trampoline) " + ExtractSignature(managedMethod),
            MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard, returnType, parameterTypes,
            typeof(DelegateSupport), true);
        var bodyBuilder = trampoline.GetILGenerator();

        var tryLabel = bodyBuilder.BeginExceptionBlock();

        bodyBuilder.Emit(OpCodes.Ldarg_0);
        bodyBuilder.Emit(OpCodes.Call,
            typeof(ClassInjectorBase).GetMethod(nameof(ClassInjectorBase.GetMonoObjectFromIl2CppPointer))!);
        bodyBuilder.Emit(OpCodes.Castclass, typeof(Il2CppToMonoDelegateReference));
        bodyBuilder.Emit(OpCodes.Ldfld,
            typeof(Il2CppToMonoDelegateReference).GetField(nameof(Il2CppToMonoDelegateReference.ReferencedDelegate)));

        for (var i = 0; i < managedParameters.Length; i++)
        {
            var parameterType = managedParameters[i].ParameterType;

            bodyBuilder.Emit(OpCodes.Ldarg, i + 1);
            if (parameterType == typeof(string))
            {
                bodyBuilder.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppStringToManaged))!);
            }
            else if (parameterType.IsInterface)
            {
                bodyBuilder.Emit(OpCodes.Call, typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!.MakeGenericMethod(parameterType));
            }
            else if (TrampolineHelpers.IsWrappedStruct(parameterType))
            {
                // A wrapped struct arrives as its data, not as a box the wrapper could point at
                bodyBuilder.Emit(OpCodes.Pop);
                bodyBuilder.Emit(OpCodes.Ldc_I8, Il2CppClassPointerStore.GetNativeClassPointer(parameterType).ToInt64());
                bodyBuilder.Emit(OpCodes.Conv_I);
                bodyBuilder.Emit(OpCodes.Ldarg, i + 1);
                bodyBuilder.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(IL2CPP.IsIl2CppNullable(parameterType) ? nameof(IL2CPP.BoxNullable) : nameof(IL2CPP.il2cpp_value_box))!);
                bodyBuilder.Emit(OpCodes.Newobj, parameterType.GetConstructor(new[] { typeof(IntPtr) })!);
            }
            else if (typeof(Il2CppObjectBase).IsAssignableFrom(parameterType) && !typeof(Il2CppArrayBase).IsAssignableFrom(parameterType))
            {
                // The pool hands back the managed object of an injected class, a fresh wrapper would lose the subclass
                bodyBuilder.Emit(OpCodes.Call, typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!.MakeGenericMethod(parameterType));
            }
            else if (!parameterType.IsValueType)
            {
                var labelNull = bodyBuilder.DefineLabel();
                var labelDone = bodyBuilder.DefineLabel();
                bodyBuilder.Emit(OpCodes.Brfalse, labelNull);
                bodyBuilder.Emit(OpCodes.Ldarg, i + 1);
                bodyBuilder.Emit(OpCodes.Newobj, parameterType.GetConstructor(new[] { typeof(IntPtr) })!);
                bodyBuilder.Emit(OpCodes.Br, labelDone);
                bodyBuilder.MarkLabel(labelNull);
                bodyBuilder.Emit(OpCodes.Ldnull);
                bodyBuilder.MarkLabel(labelDone);
            }
        }

        bodyBuilder.Emit(OpCodes.Call, managedMethod);

        if (managedMethod.ReturnType == typeof(string))
        {
            bodyBuilder.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp))!);
        }
        else if (!managedMethod.ReturnType.IsValueType)
        {
            var labelNull = bodyBuilder.DefineLabel();
            var labelDone = bodyBuilder.DefineLabel();
            bodyBuilder.Emit(OpCodes.Dup);
            bodyBuilder.Emit(OpCodes.Brfalse, labelNull);
            if (managedMethod.ReturnType.IsInterface)
                bodyBuilder.Emit(OpCodes.Castclass, typeof(Il2CppObjectBase));
            bodyBuilder.Emit(OpCodes.Call,
                typeof(Il2CppObjectBase).GetProperty(nameof(Il2CppObjectBase.Pointer))!.GetMethod);
            bodyBuilder.Emit(OpCodes.Br, labelDone);
            bodyBuilder.MarkLabel(labelNull);
            bodyBuilder.Emit(OpCodes.Pop);
            bodyBuilder.Emit(OpCodes.Ldc_I4_0);
            bodyBuilder.Emit(OpCodes.Conv_I);
            bodyBuilder.MarkLabel(labelDone);
        }

        LocalBuilder returnLocal = null;
        if (returnType != typeof(void))
        {
            returnLocal = bodyBuilder.DeclareLocal(returnType);
            bodyBuilder.Emit(OpCodes.Stloc, returnLocal);
        }

        var exceptionLocal = bodyBuilder.DeclareLocal(typeof(Exception));
        bodyBuilder.BeginCatchBlock(typeof(Exception));
        bodyBuilder.Emit(OpCodes.Stloc, exceptionLocal);
        bodyBuilder.Emit(OpCodes.Ldstr, "Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp: ");
        bodyBuilder.Emit(OpCodes.Ldloc, exceptionLocal);
        bodyBuilder.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(ToString))!);
        bodyBuilder.Emit(OpCodes.Call,
            typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Call, typeof(DelegateSupport).GetMethod(nameof(LogError), BindingFlags.Static | BindingFlags.NonPublic)!);

        bodyBuilder.EndExceptionBlock();

        if (returnLocal != null)
            bodyBuilder.Emit(OpCodes.Ldloc, returnLocal);
        bodyBuilder.Emit(OpCodes.Ret);

        return trampoline.CreateDelegate(GetOrCreateDelegateType(signature, managedMethod));
    }

    private static void LogError(string message)
    {
        Logger.Instance.LogError("{Message}", message);
    }

    public static TIl2Cpp? ConvertDelegate<TIl2Cpp>(Delegate @delegate) where TIl2Cpp : Il2CppObjectBase
    {
        if (@delegate == null)
            return null;

        if (@delegate.HasSingleTarget)
        {
            var key = (@delegate.Method, typeof(TIl2Cpp));
            var byMethod = @delegate.Target == null
                ? ConvertedStatic
                : ConvertedByTarget.GetValue(@delegate.Target, static _ => new ConcurrentDictionary<(MethodInfo, Type), Il2CppObjectBase>());
            return (TIl2Cpp)byMethod.GetOrAdd(key, static (_, source) => ConvertDelegateUncached<TIl2Cpp>(source), @delegate);
        }

        var converted = ConvertedDelegates.GetValue(@delegate, static _ => new ConcurrentDictionary<Type, Il2CppObjectBase>());
        if (converted.TryGetValue(typeof(TIl2Cpp), out var existing))
            return (TIl2Cpp)existing;

        var result = ConvertDelegateUncached<TIl2Cpp>(@delegate);
        converted[typeof(TIl2Cpp)] = result;
        return result;
    }

    // What a conversion needs that depends only on the managed and il2cpp delegate types
    private sealed class ConversionPlan
    {
        public IntPtr ClassPointer;
        public Il2CppSystem.Reflection.MethodInfo NativeInvokeMethod = null!;
        public IntPtr MethodInfo;
        public IntPtr MethodPointer;
    }

    // The checks and il2cpp reflection behind a plan cost several microseconds, so each pair is planned once. Its
    // native MethodInfo is shared by every conversion where each used to allocate one.
    private static readonly ConcurrentDictionary<(Type Managed, Type Native), ConversionPlan> ConversionPlans = new();

    // Activator searched for this constructor on every conversion
    private static class DelegateConstructor<TIl2Cpp>
    {
        public static readonly Func<Object, IntPtr, TIl2Cpp> Create = Build();

        private static Func<Object, IntPtr, TIl2Cpp> Build()
        {
            var target = Expression.Parameter(typeof(Object));
            var method = Expression.Parameter(typeof(IntPtr));
            var constructor = typeof(TIl2Cpp).GetConstructor([typeof(Object), typeof(IntPtr)])!;
            return Expression.Lambda<Func<Object, IntPtr, TIl2Cpp>>(Expression.New(constructor, target, method), target, method)
                .Compile();
        }
    }

    private static TIl2Cpp ConvertDelegateUncached<TIl2Cpp>(Delegate @delegate) where TIl2Cpp : Il2CppObjectBase
    {
        var plan = ConversionPlans.GetOrAdd((@delegate.GetType(), typeof(TIl2Cpp)),
            static key => CreateConversionPlan(key.Managed, key.Native));
        var delegateReference = new Il2CppToMonoDelegateReference(@delegate);

        Il2CppSystem.Delegate converted;
        if (UnityVersionHandler.MustUseDelegateConstructor)
            converted = DelegateConstructor<TIl2Cpp>.Create(delegateReference, plan.MethodInfo).Cast<Il2CppSystem.Delegate>();
        else
            converted = new Il2CppSystem.Delegate(IL2CPP.il2cpp_object_new(plan.ClassPointer));

        converted.method_ptr = plan.MethodPointer;
        converted.method_info = plan.NativeInvokeMethod; // todo: is this truly a good hack?
        converted.method = plan.MethodInfo;
        converted.m_target = delegateReference;

        if (UnityVersionHandler.MustUseDelegateConstructor)
        {
            // U2021.2.0+ hack in case the constructor did the wrong thing anyway
            converted.invoke_impl = converted.method_ptr;
            converted.method_code = converted.m_target.Pointer;
        }

        return converted.Cast<TIl2Cpp>();
    }

    private static ConversionPlan CreateConversionPlan(Type managedDelegateType, Type il2CppDelegateClrType)
    {
        if (!typeof(Il2CppSystem.Delegate).IsAssignableFrom(il2CppDelegateClrType))
            throw new ArgumentException($"{il2CppDelegateClrType} is not a delegate");

        var managedInvokeMethod = managedDelegateType.GetMethod("Invoke")!;
        if (TrampolineHelpers.IsWrappedStruct(managedInvokeMethod.ReturnType))
            throw new ArgumentException(
                $"Delegate returns {managedInvokeMethod.ReturnType} (non-blittable struct) which is not supported");

        var parameterInfos = managedInvokeMethod.GetParameters();
        foreach (var parameterInfo in parameterInfos)
        {
            var parameterType = parameterInfo.ParameterType;
            if (parameterType.IsGenericParameter)
                throw new ArgumentException(
                    $"Delegate has unsubstituted generic parameter ({parameterType}) which is not supported");

            // Fully shared generic invokes pass even small structs by pointer, so a register sized struct is ambiguous
            if (TrampolineHelpers.IsPassedByValue(parameterType))
                throw new ArgumentException(
                    $"Delegate has parameter of type {parameterType} (register sized struct) which is not supported");
        }

        var classTypePtr = Il2CppClassPointerStore.GetNativeClassPointer(il2CppDelegateClrType);
        if (classTypePtr == IntPtr.Zero)
            throw new ArgumentException($"Type {il2CppDelegateClrType} has uninitialized class pointer");

        if (Il2CppClassPointerStore<Il2CppToMonoDelegateReference>.NativeClassPtr == IntPtr.Zero)
            ClassInjector.RegisterTypeInIl2Cpp<Il2CppToMonoDelegateReference>();

        var il2CppDelegateType = Il2CppSystem.Type.internal_from_handle(IL2CPP.il2cpp_class_get_type(classTypePtr));
        var nativeDelegateInvokeMethod = il2CppDelegateType.GetMethod("Invoke");

        var nativeParameters = nativeDelegateInvokeMethod.GetParameters();
        if (nativeParameters.Count != parameterInfos.Length)
            throw new ArgumentException(
                $"Managed delegate has {parameterInfos.Length} parameters, native has {nativeParameters.Count}, these should match");

        for (var i = 0; i < nativeParameters.Count; i++)
        {
            var nativeType = nativeParameters[i].ParameterType;
            var managedType = parameterInfos[i].ParameterType;

            if (nativeType.IsPrimitive || managedType.IsPrimitive)
            {
                if (nativeType.FullName != managedType.FullName)
                    throw new ArgumentException(
                        $"Parameter type mismatch at parameter {i}: {nativeType.FullName} != {managedType.FullName}");

                continue;
            }

            var classPointerFromManagedType = Il2CppClassPointerStore.GetNativeClassPointer(managedType);

            var classPointerFromNativeType = IL2CPP.il2cpp_class_from_type(nativeType._impl.value);

            if (classPointerFromManagedType != classPointerFromNativeType)
                throw new ArgumentException(
                    $"Parameter type at {i} has mismatched native type pointers; types: {nativeType.FullName} != {managedType.FullName}");

            if (nativeType.IsByRef || managedType.IsByRef)
                throw new ArgumentException($"Parameter at {i} is passed by reference, this is not supported");
        }

        var signature = new MethodSignature(nativeDelegateInvokeMethod, true);
        var managedTrampoline =
            GetOrCreateNativeToManagedTrampoline(signature, nativeDelegateInvokeMethod, managedInvokeMethod);

        var methodInfo = UnityVersionHandler.NewMethod();
        methodInfo.MethodPointer = Marshal.GetFunctionPointerForDelegate(managedTrampoline);
        methodInfo.ParametersCount = (byte)parameterInfos.Length;
        methodInfo.Slot = ushort.MaxValue;
        methodInfo.IsMarshalledFromNative = true;

        return new ConversionPlan
        {
            ClassPointer = classTypePtr,
            NativeInvokeMethod = nativeDelegateInvokeMethod,
            MethodInfo = methodInfo.Pointer,
            MethodPointer = methodInfo.MethodPointer,
        };
    }

    internal class MethodSignature : IEquatable<MethodSignature>
    {
        public readonly bool ConstructedFromNative;
        public readonly bool HasThis;
        public readonly bool HasReturnBuffer;
        private readonly int _hashCode;

        // The return type, the declaring type when there is a this, then every parameter type. il2cpp types are
        // kept as their class pointers, which are unique per type.
        private readonly object[] _types;

        public MethodSignature(Il2CppSystem.Reflection.MethodInfo methodInfo, bool hasThis)
        {
            HasThis = hasThis;
            ConstructedFromNative = true;

            List<object> types = [methodInfo.ReturnType.Pointer];
            if (hasThis) types.Add(methodInfo.DeclaringType.Pointer);
            foreach (var parameterInfo in methodInfo.GetParameters())
                types.Add(parameterInfo.ParameterType.Pointer);

            _types = types.ToArray();
            _hashCode = HashTypes(_types, false);
        }

        public MethodSignature(MethodInfo methodInfo, bool hasThis)
        {
            HasThis = hasThis;
            ConstructedFromNative = false;
            HasReturnBuffer = TrampolineHelpers.NeedsReturnBuffer(methodInfo.ReturnType);

            List<object> types = [methodInfo.ReturnType.NativeType()];
            if (hasThis) types.Add(methodInfo.DeclaringType.NativeType());
            foreach (var parameterInfo in methodInfo.GetParameters())
                types.Add(parameterInfo.ParameterType.NativeType());

            _types = types.ToArray();
            _hashCode = HashTypes(_types, HasReturnBuffer);
        }

        private static int HashTypes(object[] types, bool hasReturnBuffer)
        {
            var hashCode = new HashCode();
            hashCode.Add(hasReturnBuffer);
            foreach (var type in types)
                hashCode.Add(type);
            return hashCode.ToHashCode();
        }

        public override int GetHashCode()
        {
            return _hashCode;
        }

        public bool Equals(MethodSignature other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return _hashCode == other._hashCode && HasThis == other.HasThis && HasReturnBuffer == other.HasReturnBuffer &&
                   ConstructedFromNative == other.ConstructedFromNative && _types.AsSpan().SequenceEqual(other._types);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MethodSignature)obj);
        }

        public static bool operator ==(MethodSignature left, MethodSignature right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(MethodSignature left, MethodSignature right)
        {
            return !Equals(left, right);
        }
    }

    // The native MethodInfo belongs to the conversion plan and is shared, so the reference no longer frees one
    private class Il2CppToMonoDelegateReference : Object
    {
        public Delegate ReferencedDelegate;

        public Il2CppToMonoDelegateReference(IntPtr obj0) : base(obj0)
        {
        }

        public Il2CppToMonoDelegateReference(Delegate referencedDelegate) : base(
            ClassInjector.DerivedConstructorPointer<Il2CppToMonoDelegateReference>())
        {
            ClassInjector.DerivedConstructorBody(this);

            ReferencedDelegate = referencedDelegate;
        }
    }
}
