using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;
using MonoMod.Cil;
using MonoMod.Utils;
using ValueType = Il2CppSystem.ValueType;
using Void = Il2CppSystem.Void;

namespace Il2CppInterop.HarmonySupport;

internal unsafe class Il2CppDetourMethodPatcher : MethodPatcher
{
    private static readonly MethodInfo IL2CPPToManagedStringMethodInfo
        = AccessTools.Method(typeof(IL2CPP),
            nameof(IL2CPP.Il2CppStringToManaged));

    private static readonly MethodInfo ManagedToIL2CPPStringMethodInfo
        = AccessTools.Method(typeof(IL2CPP),
            nameof(IL2CPP.ManagedStringToIl2Cpp));

    private static readonly MethodInfo ObjectBaseToPtrMethodInfo
        = AccessTools.Method(typeof(IL2CPP),
            nameof(IL2CPP.Il2CppObjectBaseToPtr));

    private static readonly MethodInfo ObjectBaseToPtrNotNullMethodInfo
        = AccessTools.Method(typeof(IL2CPP),
            nameof(IL2CPP.Il2CppObjectBaseToPtrNotNull));

    private static readonly MethodInfo ReportExceptionMethodInfo
        = AccessTools.Method(typeof(Il2CppDetourMethodPatcher), nameof(ReportException));

    // Map each value type to correctly sized store opcode to prevent memory overwrite
    // Special case: bool is byte in Il2Cpp
    private static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
    {
        [typeof(byte)] = OpCodes.Stind_I1,
        [typeof(sbyte)] = OpCodes.Stind_I1,
        [typeof(bool)] = OpCodes.Stind_I1,
        [typeof(short)] = OpCodes.Stind_I2,
        [typeof(ushort)] = OpCodes.Stind_I2,
        [typeof(int)] = OpCodes.Stind_I4,
        [typeof(uint)] = OpCodes.Stind_I4,
        [typeof(long)] = OpCodes.Stind_I8,
        [typeof(ulong)] = OpCodes.Stind_I8,
        [typeof(float)] = OpCodes.Stind_R4,
        [typeof(double)] = OpCodes.Stind_R8
    };

    private static readonly List<object> DelegateCache = new();
    private INativeMethodInfoStruct modifiedNativeMethodInfo;

    private IDetour nativeDetour;

    private INativeMethodInfoStruct originalNativeMethodInfo;

    /// <summary>
    ///     Constructs a new instance of <see cref="MonoMod.RuntimeDetour.NativeDetour" /> method patcher.
    /// </summary>
    /// <param name="original"></param>
    public Il2CppDetourMethodPatcher(MethodBase original) : base(original) => Init();

    internal bool IsValid { get; private set; }

    private void Init()
    {
        try
        {
            var methodField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(Original);

            if (methodField == null)
            {
                var fieldInfoField =
                    Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(Original);

                if (fieldInfoField != null)
                {
                    throw new
                        Exception($"Method {Original.FullDescription()} is a field accessor, it can't be patched.");
                }

                // Generated method is probably unstripped, it can be safely handed to IL handler
                return;
            }

            // Get the native MethodInfo struct for the target method
            originalNativeMethodInfo =
                UnityVersionHandler.Wrap((Il2CppMethodInfo*)(IntPtr)methodField.GetValue(null));

            // Create a modified native MethodInfo struct, that will point towards the trampoline
            modifiedNativeMethodInfo = UnityVersionHandler.NewMethod();
            Buffer.MemoryCopy(originalNativeMethodInfo.Pointer.ToPointer(),
                modifiedNativeMethodInfo.Pointer.ToPointer(), UnityVersionHandler.MethodSize(),
                UnityVersionHandler.MethodSize());
            IsValid = true;
        }
        catch (Exception e)
        {
            Logger.Instance.LogWarning(
                "Failed to init IL2CPP patch backend for {Original}, using normal patch handlers: {ErrorMessage}",
                Original.FullDescription(), e.Message);
        }
    }

    /// <inheritdoc />
    public override DynamicMethodDefinition PrepareOriginal() => null;

    /// <inheritdoc />
    public override MethodBase DetourTo(MethodBase replacement)
    {
        // // Unpatch an existing detour if it exists
        if (nativeDetour != null)
        {
            // Point back to the original method before we unpatch
            modifiedNativeMethodInfo.MethodPointer = originalNativeMethodInfo.MethodPointer;
            nativeDetour.Dispose();
        }

        // Generate a new DMD of the modified unhollowed method, and apply harmony patches to it
        var copiedDmd = CopyOriginal();

        HarmonyManipulator.Manipulate(copiedDmd.OriginalMethod, copiedDmd.OriginalMethod.GetPatchInfo(),
            new ILContext(copiedDmd.Definition));

        // Generate the MethodInfo instances
        var managedHookedMethod = copiedDmd.Generate();
        var unmanagedTrampolineMethod = GenerateNativeToManagedTrampoline(managedHookedMethod).Generate();

        // Apply a detour from the unmanaged implementation to the patched harmony method
        var unmanagedDelegateType = DelegateTypeFactory.instance.CreateDelegateType(unmanagedTrampolineMethod,
            CallingConvention.Cdecl);

        var unmanagedDelegate = unmanagedTrampolineMethod.CreateDelegate(unmanagedDelegateType);
        DelegateCache.Add(unmanagedDelegate);

        nativeDetour =
            Il2CppInteropRuntime.Instance.DetourProvider.Create(originalNativeMethodInfo.MethodPointer, unmanagedDelegate);
        nativeDetour.Apply();
        modifiedNativeMethodInfo.MethodPointer = nativeDetour.OriginalTrampoline;

        // TODO: Add an ILHook for the original unhollowed method to go directly to managedHookedMethod
        // Right now it goes through three times as much interop conversion as it needs to, when being called from managed side
        return managedHookedMethod;
    }

    /// <inheritdoc />
    public override DynamicMethodDefinition CopyOriginal()
    {
        var dmd = new DynamicMethodDefinition(Original);
        dmd.Definition.Name = "UnhollowedWrapper_" + dmd.Definition.Name;
        var cursor = new ILCursor(new ILContext(dmd.Definition));


        // Remove il2cpp_object_get_virtual_method
        if (cursor.TryGotoNext(x => x.MatchLdarg(0),
                x => x.MatchCall(typeof(IL2CPP),
                    nameof(IL2CPP.Il2CppObjectBaseToPtr)),
                x => x.MatchLdsfld(out _),
                x => x.MatchCall(typeof(IL2CPP),
                    nameof(IL2CPP.il2cpp_object_get_virtual_method))))
        {
            cursor.RemoveRange(4);
        }
        else
        {
            cursor.Goto(0)
                .GotoNext(x =>
                    x.MatchLdsfld(Il2CppInteropUtils
                        .GetIl2CppMethodInfoPointerFieldForGeneratedMethod(Original)))
                .Remove();
        }

        // Replace original IL2CPPMethodInfo pointer with a modified one that points to the trampoline
        cursor
            .Emit(Mono.Cecil.Cil.OpCodes.Ldc_I8, modifiedNativeMethodInfo.Pointer.ToInt64())
            .Emit(Mono.Cecil.Cil.OpCodes.Conv_I);

        return dmd;
    }

    // Tries to guess whether a function needs a return buffer for the return struct, in all cases except win64 it's undefined behaviour
    private static bool IsReturnBufferNeeded(int size)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170#return-values
            return size != 1 && size != 2 && size != 4 && size != 8;
        }

        if (Environment.Is64BitProcess)
        {
            // x64 gcc and clang seem to use a return buffer for everything above 16 bytes
            return size > 16;
        }

        // Looks like on x32 gcc and clang return buffer is always used
        return true;
    }

    private DynamicMethodDefinition GenerateNativeToManagedTrampoline(MethodInfo targetManagedMethodInfo)
    {
        // managedParams are the interop types used on the managed side
        // unmanagedParams are IntPtr references that are used by IL2CPP compiled assembly
        var paramStartIndex = 0;

        var managedReturnType = AccessTools.GetReturnedType(Original);
        var unmanagedReturnType = managedReturnType.NativeType();

        var returnSize = IntPtr.Size;

        var isReturnValueType = IsWrappedValueType(managedReturnType);
        if (isReturnValueType)
        {
            uint align = 0;
            returnSize = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore.GetNativeClassPointer(managedReturnType), ref align);
        }

        var hasReturnBuffer = isReturnValueType && IsReturnBufferNeeded(returnSize);
        if (hasReturnBuffer)
        // C compilers seem to return large structs by allocating a return buffer on caller's side and passing it as the first parameter
        // TODO: Handle ARM
        // TODO: Check if this applies to values other than structs
        {
            unmanagedReturnType = typeof(IntPtr);
            paramStartIndex++;
        }

        if (!Original.IsStatic)
        {
            paramStartIndex++;
        }

        var managedParams = Original.GetParameters().Select(x => x.ParameterType).ToArray();
        var unmanagedParams =
            new Type[managedParams.Length + paramStartIndex +
                     1]; // +1 for methodInfo at the end

        if (hasReturnBuffer)
        // With GCC the return buffer seems to be the first param, same is likely with other compilers too
        {
            unmanagedParams[0] = typeof(IntPtr);
        }

        if (!Original.IsStatic)
        {
            unmanagedParams[paramStartIndex - 1] = typeof(IntPtr);
        }

        unmanagedParams[^1] = typeof(Il2CppMethodInfo*);
        Array.Copy(managedParams.Select(TrampolineHelpers.NativeType).ToArray(), 0,
            unmanagedParams, paramStartIndex, managedParams.Length);

        var dmd = new DynamicMethodDefinition("(il2cpp -> managed) " + Original.Name,
            unmanagedReturnType,
            unmanagedParams
        );

        var il = dmd.GetILGenerator();
        il.BeginExceptionBlock();

        // Declare a list of variables to dereference back to the original pointers.
        // This is required due to the needed interop type conversions, so we can't directly pass some addresses as byref types
        var indirectVariables = new LocalBuilder[managedParams.Length];

        if (!Original.IsStatic)
        {
            EmitConvertArgumentToManaged(il, paramStartIndex - 1, Original.DeclaringType, out _);
        }

        for (var i = 0; i < managedParams.Length; ++i)
        {
            EmitConvertArgumentToManaged(il, i + paramStartIndex, managedParams[i], out indirectVariables[i]);
        }

        // Run the managed method
        il.Emit(OpCodes.Call, targetManagedMethodInfo);

        // Store the managed return type temporarily (if there was one)
        LocalBuilder managedReturnVariable = null;
        if (managedReturnType != typeof(void))
        {
            managedReturnVariable = il.DeclareLocal(managedReturnType);
            il.Emit(OpCodes.Stloc, managedReturnVariable);
        }

        // Convert any managed byref values into their relevant IL2CPP types, and then store the values into their relevant dereferenced pointers
        for (var i = 0; i < managedParams.Length; ++i)
        {
            if (indirectVariables[i] == null)
            {
                continue;
            }

            var directType = managedParams[i].GetElementType();
            il.Emit(OpCodes.Ldarg_S, i + paramStartIndex);

            if (IsWrappedValueType(directType))
            {
                // The managed method was handed a boxed copy, since a wrapped value type cannot alias the
                // caller's storage; copy the box back over that storage, the same way a returned struct is
                // copied into the caller's return buffer below.
                //
                // This copy carries no GC write barrier. il2cpp exports only a single field barrier and no
                // range form, so where the caller's storage is inside a managed heap object, references the
                // struct carries can be missed by an incremental mark already in progress.
                uint align = 0;
                var valueSize = IL2CPP.il2cpp_class_value_size(
                    Il2CppClassPointerStore.GetNativeClassPointer(directType), ref align);

                il.Emit(OpCodes.Ldloc, indirectVariables[i]);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldc_I4, valueSize);
                il.Emit(OpCodes.Cpblk);
                continue;
            }

            il.Emit(OpCodes.Ldloc, indirectVariables[i]);
            EmitConvertManagedTypeToIL2CPP(il, directType);
            il.Emit(StIndOpcodes.TryGetValue(directType, out var stindOpCodde) ? stindOpCodde : OpCodes.Stind_I);
        }

        // Handle any lingering exceptions
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Call, ReportExceptionMethodInfo);
        il.EndExceptionBlock();

        // Convert the return value back to an IL2CPP friendly type (if there was a return value), and then return
        if (managedReturnVariable != null)
        {
            if (hasReturnBuffer)
            {
                // This runs after the catch block, so the return is null whenever the managed method threw
                var returnBufferEmpty = il.DefineLabel();
                var returnBufferFilled = il.DefineLabel();

                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Brfalse, returnBufferEmpty);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldc_I4, returnSize);
                il.Emit(OpCodes.Cpblk);
                il.Emit(OpCodes.Br, returnBufferFilled);

                il.MarkLabel(returnBufferEmpty);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, returnSize);
                il.Emit(OpCodes.Initblk);

                il.MarkLabel(returnBufferFilled);

                // Return the same pointer to the return buffer
                il.Emit(OpCodes.Ldarg_0);
            }
            else if (TrampolineHelpers.IsPassedByValue(managedReturnType))
            {
                // A small struct goes back in a register, so the boxed payload is copied out as the fixed size
                // struct. Same as above, a throwing patch leaves nothing to copy and has to yield a zeroed value.
                var registerValue = il.DeclareLocal(unmanagedReturnType);
                var registerValueSet = il.DefineLabel();

                il.Emit(OpCodes.Ldloca, registerValue);
                il.Emit(OpCodes.Initobj, unmanagedReturnType);

                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Brfalse, registerValueSet);

                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldobj, unmanagedReturnType);
                il.Emit(OpCodes.Stloc, registerValue);

                il.MarkLabel(registerValueSet);
                il.Emit(OpCodes.Ldloc, registerValue);
            }
            else
            {
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                EmitConvertManagedTypeToIL2CPP(il, managedReturnType);
            }
        }

        il.Emit(OpCodes.Ret);

        return dmd;
    }

    private static void EmitUnbox(ILGenerator il)
    {
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sizeof, typeof(void*));
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
    }

    private static void ReportException(Exception ex) =>
        Logger.Instance.LogError(ex, "During invoking native->managed trampoline");

    private static void EmitConvertManagedTypeToIL2CPP(ILGenerator il, Type returnType)
    {
        if (returnType == typeof(string))
        {
            il.Emit(OpCodes.Call, ManagedToIL2CPPStringMethodInfo);
        }
        else if (!returnType.IsValueType && returnType.IsSubclassOf(typeof(Il2CppObjectBase)))
        {
            il.Emit(OpCodes.Call, ObjectBaseToPtrMethodInfo);
        }
        else if (returnType.IsInterface)
        {
            il.Emit(OpCodes.Castclass, typeof(Il2CppObjectBase));
            il.Emit(OpCodes.Call, ObjectBaseToPtrMethodInfo);
        }
    }

    private static void EmitConvertArgumentToManaged(ILGenerator il,
        int argIndex,
        Type managedParamType,
        out LocalBuilder variable)
    {
        variable = null;

        bool needsBoxing = IsWrappedValueType(managedParamType);

        if (needsBoxing)
        {
            EmitBoxWrappedValueType(il, managedParamType,
                () => il.Emit(TrampolineHelpers.IsPassedByValue(managedParamType) ? OpCodes.Ldarga_S : OpCodes.Ldarg, argIndex));
        }
        else
        {
            il.Emit(OpCodes.Ldarg_S, argIndex);
        }

        if (managedParamType.IsValueType) // don't need to convert blittable types
        {
            return;
        }

        void EmitCreateIl2CppObject(Type originalType)
        {
            var endLabel = il.DefineLabel();
            var notNullLabel = il.DefineLabel();

            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, notNullLabel);

            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Br_S, endLabel);

            il.MarkLabel(notNullLabel);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(Il2CppObjectPool), nameof(Il2CppObjectPool.Get)).MakeGenericMethod(originalType));

            il.MarkLabel(endLabel);
        }

        void HandleTypeConversion(Type originalType)
        {
            if (originalType == typeof(string))
            {
                il.Emit(OpCodes.Call, IL2CPPToManagedStringMethodInfo);
            }
            else if (originalType.IsSubclassOf(typeof(Il2CppObjectBase)) || originalType.IsInterface)
            {
                EmitCreateIl2CppObject(originalType);
            }
        }

        if (managedParamType.IsByRef)
        {
            var directType = managedParamType.GetElementType();
            // blittable value type pointer, it is already storage of the right shape and is passed straight through
            if (directType.IsValueType)
                return;

            if (IsWrappedValueType(directType))
            {
                // A value type that isn't blittable is wrapped in a class, so the managed method has no way to
                // alias the caller's storage. Box a copy of it on the way in; GenerateNativeToManagedTrampoline
                // copies that box back over the caller's storage once the method has returned. Without this the
                // pointer is read as though it were an object reference, which is neither the caller's value nor
                // the right size.
                var storage = il.DeclareLocal(typeof(IntPtr));

                il.Emit(OpCodes.Stloc, storage);
                EmitBoxWrappedValueType(il, directType, () => il.Emit(OpCodes.Ldloc, storage));

                variable = il.DeclareLocal(directType);

                HandleTypeConversion(directType);

                il.Emit(OpCodes.Stloc, variable);
                il.Emit(OpCodes.Ldloca, variable);
                return;
            }

            variable = il.DeclareLocal(directType);

            il.Emit(OpCodes.Ldind_I);

            HandleTypeConversion(directType);

            il.Emit(OpCodes.Stloc, variable);
            il.Emit(OpCodes.Ldloca, variable);
        }
        else
        {
            HandleTypeConversion(managedParamType);
        }
    }

    /// <summary>
    ///     Whether il2cpp passes this type as a struct rather than as an object reference.
    /// </summary>
    /// <remarks>
    ///     Il2CppSystem.Enum derives from the value type wrapper, because System.Enum derives from
    ///     System.ValueType, yet il2cpp declares its class a reference and gives it no payload of its own.
    ///     Deriving from the wrapper is therefore not the question to ask; what il2cpp says about the class is.
    /// </remarks>
    private static bool IsWrappedValueType(Type managedType)
    {
        if (!managedType.IsSubclassOf(typeof(ValueType)))
            return false;

        var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedType);
        return classPtr != IntPtr.Zero && IL2CPP.il2cpp_class_is_valuetype(classPtr);
    }

    /// <summary>
    ///     Boxes the wrapped value type at the address <paramref name="emitValueAddress" /> pushes, leaving the
    ///     box's pointer on the stack.
    /// </summary>
    /// <remarks>
    ///     il2cpp_value_box is the path for every type it can represent, because Object::Box runs a GC write
    ///     barrier over the payload it copies and a plain copy does not: an incremental mark already in progress
    ///     could otherwise miss the references a struct carries. Nullable is the one type it cannot represent,
    ///     since .NET boxing semantics turn a Nullable&lt;T&gt; into a bare T or null and so lose HasValue, and it
    ///     has to allocate the box and copy the struct's own bytes instead.
    /// </remarks>
    private static void EmitBoxWrappedValueType(ILGenerator il, Type managedType, Action emitValueAddress)
    {
        var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedType);

        il.Emit(OpCodes.Ldc_I8, classPtr.ToInt64());
        il.Emit(OpCodes.Conv_I);

        if (!IsNullable(managedType))
        {
            emitValueAddress();
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.il2cpp_value_box)));
            return;
        }

        uint align = 0;
        var valueSize = IL2CPP.il2cpp_class_value_size(classPtr, ref align);

        il.Emit(OpCodes.Call, AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.il2cpp_object_new)));
        var box = il.DeclareLocal(typeof(IntPtr));
        il.Emit(OpCodes.Stloc, box);

        emitValueAddress();
        il.Emit(OpCodes.Ldloc, box);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.il2cpp_object_unbox)));
        il.Emit(OpCodes.Ldc_I4, valueSize);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(Il2CppDetourMethodPatcher), nameof(CopyMemory)));

        il.Emit(OpCodes.Ldloc, box);
    }

    // A Nullable's HasValue lives outside what .NET boxing semantics carry, so it cannot go through
    // il2cpp_value_box
    private static bool IsNullable(Type managedType) =>
        managedType.IsGenericType &&
        managedType.GetGenericTypeDefinition().FullName == "Il2CppSystem.Nullable`1";

    private static void CopyMemory(IntPtr src, IntPtr dest, int size) =>
        Buffer.MemoryCopy(src.ToPointer(), dest.ToPointer(), size, size);
}
