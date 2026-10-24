using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;
using MonoMod.Cil;
using MonoMod.Utils;
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
        // il2cpp shares one native body between the reference type instantiations of a generic method, so a detour
        // on one of them receives the calls of all the others with the wrong argument types
        if (Original is MethodInfo { IsGenericMethod: true })
        {
            Logger.Instance.LogWarning(
                "Patching the generic method {Method} also patches the other instantiations that share its il2cpp body, patch a non generic method instead",
                Original.FullDescription());
        }
        else if (Original.DeclaringType != null && Original.DeclaringType.IsGenericType &&
                 Original.DeclaringType.GetGenericArguments().Any(x => !x.IsValueType))
        {
            Logger.Instance.LogWarning(
                "Patching {Method} on a generic class also patches every reference type instantiation of that class, they share one il2cpp body",
                Original.FullDescription());
        }

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
            SharedBodyCheck.Report(Original, originalNativeMethodInfo.MethodPointer);
            WarnIfInlined(originalNativeMethodInfo.MethodPointer);

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

        PatchSignatureCheck.Report(Original, copiedDmd.OriginalMethod.GetPatchInfo());

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

        // A direct call enters the native body at its detoured entry, so the copy always takes the invoke path
        while (cursor.TryGotoNext(x => x.MatchLdsfld(out var field) && field.Name.StartsWith("NativeDirectCallPtr_")))
        {
            cursor.Remove();
            cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0).Emit(Mono.Cecil.Cil.OpCodes.Conv_I);
        }

        cursor.Goto(0);

        // A class constructor wrapper starts with this(il2cpp_object_new(classPtr)), which allocates the object a
        // managed `new` constructs. Here `this` is the object the game is already constructing, so that allocation
        // would run the native constructor on a new object and leave the game's one unconstructed
        if (Original.IsConstructor && !Original.IsStatic && Original.DeclaringType is { IsValueType: false } &&
            cursor.TryGotoNext(x => x.MatchLdarg(0),
                x => x.MatchLdsfld(out _),
                x => x.MatchCall(typeof(IL2CPP), nameof(IL2CPP.il2cpp_object_new)),
                x => x.MatchCall(out var chained) && chained.Name == ".ctor"))
            cursor.RemoveRange(4);

        cursor.Goto(0);

        // Remove the virtual lookup. It would resolve back to the detoured method and recurse
        if (cursor.TryGotoNext(x => x.MatchLdarg(0),
                x => x.MatchCall(typeof(IL2CPP),
                    nameof(IL2CPP.Il2CppObjectBaseToPtr)),
                x => x.MatchLdsfld(out _),
                x => x.MatchCall(typeof(IL2CPP), nameof(IL2CPP.ResolveVirtualMethod)) ||
                     x.MatchCall(typeof(IL2CPP), nameof(IL2CPP.il2cpp_object_get_virtual_method))))
        {
            cursor.RemoveRange(4);
            EmitTrampolineMethodInfo(cursor);
            return dmd;
        }

        // A wrapper with a direct call loads the method info for it as well, ahead of the invoke
        var methodInfoField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(Original);
        cursor.GotoNext(x => x.MatchLdsfld(methodInfoField));
        do
        {
            cursor.Remove();
            EmitTrampolineMethodInfo(cursor);
        } while (cursor.TryGotoNext(x => x.MatchLdsfld(methodInfoField)));

        return dmd;
    }

    // The modified method info points at the trampoline to the original code rather than at the detour
    private void EmitTrampolineMethodInfo(ILCursor cursor)
    {
        cursor
            .Emit(Mono.Cecil.Cil.OpCodes.Ldc_I8, modifiedNativeMethodInfo.Pointer.ToInt64())
            .Emit(Mono.Cecil.Cil.OpCodes.Conv_I);
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

    public static void GuardStack(string method)
    {
        if (System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return;
        }

        throw new InsufficientExecutionStackException(
            $"The patch on {method} ran out of stack. It is most likely calling its own target again. Stack:{Environment.NewLine}{new System.Diagnostics.StackTrace(false)}");
    }

    private DynamicMethodDefinition GenerateNativeToManagedTrampoline(MethodInfo targetManagedMethodInfo)
    {
        // managedParams are the interop types used on the managed side
        // unmanagedParams are IntPtr references that are used by IL2CPP compiled assembly
        var paramStartIndex = 0;

        var managedReturnType = AccessTools.GetReturnedType(Original);
        var unmanagedReturnType = managedReturnType.NativeType();

        var returnSize = IntPtr.Size;

        var isReturnValueType = TrampolineHelpers.IsWrappedStruct(managedReturnType);
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

        // A patch that ends up calling its own target again recurses until the stack dies, which kills the process silently
        il.Emit(OpCodes.Ldstr, $"{Original.DeclaringType?.FullName}.{Original.Name}");
        il.Emit(OpCodes.Call, typeof(Il2CppDetourMethodPatcher).GetMethod(nameof(GuardStack))!);

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

            if (TrampolineHelpers.IsBoxedStructByRef(managedParams[i]))
            {
                il.Emit(OpCodes.Ldloc, indirectVariables[i]);
                il.Emit(OpCodes.Ldarg_S, i + paramStartIndex);
                TrampolineHelpers.EmitCopyBoxedStructTo(il, managedParams[i]);
                continue;
            }

            il.Emit(OpCodes.Ldarg_S, i + paramStartIndex);
            il.Emit(OpCodes.Ldloc, indirectVariables[i]);
            var directType = managedParams[i].GetElementType();
            EmitConvertManagedTypeToIL2CPP(il, directType);
            if (!directType.IsValueType)
                il.Emit(OpCodes.Call, AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.WriteByRef)));
            else
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
                // A prefix that skips the original without setting __result, or an exception, leaves the wrapper null.
                // The struct's default is all zeroes.
                var copyLabel = il.DefineLabel();
                var doneLabel = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Brtrue_S, copyLabel);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, returnSize);
                il.Emit(OpCodes.Initblk);
                il.Emit(OpCodes.Br_S, doneLabel);

                il.MarkLabel(copyLabel);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldc_I4, returnSize);
                il.Emit(OpCodes.Cpblk);

                // Return the same pointer to the return buffer
                il.MarkLabel(doneLabel);
                il.Emit(OpCodes.Ldarg_0);
            }
            else if (TrampolineHelpers.IsPassedByValue(managedReturnType))
            {
                // A small struct goes back in a register, so the boxed payload is copied out as the fixed size struct
                var copyLabel = il.DefineLabel();
                var doneLabel = il.DefineLabel();
                var zero = il.DeclareLocal(unmanagedReturnType);
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Brtrue_S, copyLabel);
                il.Emit(OpCodes.Ldloca, zero);
                il.Emit(OpCodes.Initobj, unmanagedReturnType);
                il.Emit(OpCodes.Ldloc, zero);
                il.Emit(OpCodes.Br_S, doneLabel);

                il.MarkLabel(copyLabel);
                il.Emit(OpCodes.Ldloc, managedReturnVariable);
                il.Emit(OpCodes.Call, ObjectBaseToPtrNotNullMethodInfo);
                EmitUnbox(il);
                il.Emit(OpCodes.Ldobj, unmanagedReturnType);
                il.MarkLabel(doneLabel);
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

    /// <summary>
    ///     Warn when the game carries copies of the target inside other functions. Calls from those never reach the patch
    /// </summary>
    /// <param name="body">Native body of the target</param>
    private void WarnIfInlined(IntPtr body)
    {
        var inlined = Original.GetCustomAttribute<InlinedIntoAttribute>();
        if (inlined != null)
        {
            ReportInlined(inlined);
            return;
        }

        // Virtual and interface calls go through the vtable and are not counted
        if (Original is not MethodInfo method || method.IsVirtual || method.IsAbstract || IsEngineMessage(method))
            return;

        var callers = method.GetCustomAttribute<CallerCountAttribute>();
        if (callers == null || callers.Count != 0)
            return;

        // Delegates and native callbacks reach a method without a direct call too, those are larger functions with
        // unwind data. Only a leaf function has none, and that is what gets inlined into every caller
        if (!OperatingSystem.IsWindows() || RtlLookupFunctionEntry((ulong)body, out _, IntPtr.Zero) != IntPtr.Zero)
            return;

        Logger.Instance.LogWarning(
            "{Method} is a small function the game never calls directly, so the game has most likely inlined it. Its own calls then skip this patch and only calls through the interop reach it, patch the calling method instead",
            Original.FullDescription());
    }

    /// <summary>
    ///     Log where the generator found inlined copies of the target
    /// </summary>
    /// <param name="inlined">The generator's findings for the target</param>
    private void ReportInlined(InlinedIntoAttribute inlined)
    {
        var method = Original.FullDescription();
        if (inlined.Count > 0)
        {
            var more = inlined.Count > inlined.Callers.Length ? $" and {inlined.Count - inlined.Callers.Length} more" : "";
            var reachedIndirectly = Original.IsVirtual || Original is MethodInfo message && IsEngineMessage(message);
            var direct = inlined.DirectCalls > 0
                ? $"The patch still runs for the {inlined.DirectCalls} direct calls."
                : reachedIndirectly
                    ? "The patch still runs for virtual calls and Unity messages."
                    : "Nothing calls it directly, so the patch only runs through the interop.";
            Logger.Instance.LogWarning(
                "{Method} is inlined into {Callers}{More}. Calls from those skip this patch. {Direct} Patch the listed functions to cover them",
                method, string.Join(", ", inlined.Callers), more, direct);
            return;
        }

        // Unity calls messages by name, a delegate or reflection reaches the method the same way
        if (Original is MethodInfo info && IsEngineMessage(info))
            return;

        if (inlined.ForwardsTo != null)
        {
            Logger.Instance.LogWarning(
                "{Method} is a small method the game never calls directly. Its callers inlined it and call {Target} instead, patch that unless a delegate or reflection reaches this method",
                method, inlined.ForwardsTo);
            return;
        }

        Logger.Instance.LogWarning(
            "{Method} is a small method the game never calls directly. Its callers inlined it, patch them unless a delegate or reflection reaches this method",
            method);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);

    private static readonly HashSet<string> EngineMessages = ["Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "Reset"];

    // Unity calls these by name through reflection, so they never have a direct caller
    private static bool IsEngineMessage(MethodInfo method)
    {
        if (!EngineMessages.Contains(method.Name) && !method.Name.StartsWith("On", StringComparison.Ordinal))
            return false;

        for (var type = method.DeclaringType; type != null; type = type.BaseType)
        {
            if (type.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject" or "UnityEngine.StateMachineBehaviour")
                return true;
        }

        return false;
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

        bool needsBoxing = TrampolineHelpers.IsWrappedStruct(managedParamType);

        if (needsBoxing)
        {
            var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(managedParamType);

            // Box struct into object first before conversion
            il.Emit(OpCodes.Ldc_I8, classPtr.ToInt64());
            il.Emit(OpCodes.Conv_I);
            // We don't handle byref structs on x86 yet but we're yet to encounter those
            il.Emit(TrampolineHelpers.IsPassedByValue(managedParamType) ? OpCodes.Ldarga_S : OpCodes.Ldarg, argIndex);
            // il2cpp boxes a Nullable<T> as just its T, even a box from il2cpp_object_new reports the T class
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(IL2CPP),
                IL2CPP.IsIl2CppNullable(managedParamType) ? nameof(IL2CPP.BoxNullable) : nameof(IL2CPP.il2cpp_value_box)));
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
            // A struct argument sits in a box made above for this call alone, which a pool lookup would only miss
            il.Emit(OpCodes.Call, TrampolineHelpers.IsWrappedStruct(originalType)
                ? AccessTools.Method(typeof(Il2CppObjectBase), nameof(Il2CppObjectBase.WrapValueBox)).MakeGenericMethod(originalType)
                : AccessTools.Method(typeof(Il2CppObjectPool), nameof(Il2CppObjectPool.Get)).MakeGenericMethod(originalType));

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
            // blittable value type pointer
            if (directType.IsValueType)
                return;

            variable = il.DeclareLocal(directType);

            if (TrampolineHelpers.IsBoxedStructByRef(managedParamType))
            {
                var data = il.DeclareLocal(typeof(IntPtr));
                il.Emit(OpCodes.Stloc, data);
                il.Emit(OpCodes.Ldc_I8, Il2CppClassPointerStore.GetNativeClassPointer(directType).ToInt64());
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldloc, data);
                il.Emit(OpCodes.Call, TrampolineHelpers.BoxStructAtMethod);
            }
            else
            {
                il.Emit(OpCodes.Ldind_I);
            }

            HandleTypeConversion(directType);

            il.Emit(OpCodes.Stloc, variable);
            il.Emit(OpCodes.Ldloca, variable);
        }
        else
        {
            HandleTypeConversion(managedParamType);
        }
    }
}
