using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Type;
using Microsoft.Extensions.Logging;
using Void = Il2CppSystem.Void;

namespace Il2CppInterop.Runtime.Injection;

public unsafe class Il2CppInterfaceCollection : List<INativeClassStruct>
{
    // Managed interface per class pointer, known only when the collection was built from types
    internal readonly Dictionary<IntPtr, Type> ManagedTypes = new();

    public Il2CppInterfaceCollection(IEnumerable<INativeClassStruct> interfaces) : base(interfaces)
    {
    }

    public Il2CppInterfaceCollection(IEnumerable<Type> interfaces)
    {
        foreach (var managedType in interfaces)
        {
            var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(managedType);
            if (classPointer == IntPtr.Zero)
                throw new ArgumentException(
                    $"Type {managedType} doesn't have an IL2CPP class pointer, which means it's not an IL2CPP interface");
            Add(UnityVersionHandler.Wrap((Il2CppClass*)classPointer));
            ManagedTypes[classPointer] = managedType;
        }
    }

    public static implicit operator Il2CppInterfaceCollection(INativeClassStruct[] interfaces)
    {
        return new(interfaces);
    }

    public static implicit operator Il2CppInterfaceCollection(Type[] interfaces)
    {
        return new(interfaces);
    }
}

public class RegisterTypeOptions
{
    public static readonly RegisterTypeOptions Default = new();

    public bool LogSuccess { get; init; } = true;
    public Func<Type, Type[]>? InterfacesResolver { get; init; } = null;
    public Il2CppInterfaceCollection? Interfaces { get; init; } = null;
}

public static unsafe partial class ClassInjector
{
    /// <summary> type.FullName </summary>
    private static readonly HashSet<string> InjectedTypes = new();

    /// <summary> (method) : (method_inst, method) </summary>
    internal static readonly ConcurrentDictionary<IntPtr, (MethodInfo, ConcurrentDictionary<IntPtr, IntPtr>)>
        InflatedMethodFromContextDictionary = new();

    private static readonly ConcurrentDictionary<string, Delegate> InvokerCache = new();

    private static readonly ConcurrentDictionary<(Type type, FieldAttributes attrs), IntPtr>
        _injectedFieldTypes = new();

    private static readonly VoidCtorDelegate FinalizeDelegate = Finalize;

    public static void ProcessNewObject(Il2CppObjectBase obj)
    {
        var pointer = obj.Pointer;
        var handle = GCHandle.Alloc(obj, GCHandleType.Normal);
        AssignGcHandle(pointer, handle);
    }

    public static IntPtr DerivedConstructorPointer<T>()
    {
        // new on an unregistered class used to allocate from a null class
        if (Il2CppClassPointerStore<T>.NativeClassPtr == IntPtr.Zero)
            RegisterOnFirstUse(typeof(T));

        return IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<T>
            .NativeClassPtr); // todo: consider calling base constructor
    }

    // Which fields an injected type wraps never changes, so the reflection that finds them runs once per type
    private static readonly ConcurrentDictionary<Type, (FieldInfo Field, ConstructorInfo Constructor)[]> ourDerivedFields = new();

    public static void DerivedConstructorBody(Il2CppObjectBase objectBase)
    {
        if (objectBase.isWrapped)
            return;
        foreach (var (field, constructor) in ourDerivedFields.GetOrAdd(objectBase.GetType(), static type => FindDerivedFields(type)))
            field.SetValue(objectBase, constructor.Invoke([objectBase, field.Name]));
        var ownGcHandle = GCHandle.Alloc(objectBase, GCHandleType.Normal);
        AssignGcHandle(objectBase.Pointer, ownGcHandle);
    }

    private static (FieldInfo Field, ConstructorInfo Constructor)[] FindDerivedFields(Type type)
    {
        return type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(IsFieldEligible)
            .Select(field => (field, field.FieldType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(Il2CppObjectBase), typeof(string)], [])!))
            .ToArray();
    }

    public static void AssignGcHandle(IntPtr pointer, GCHandle gcHandle)
    {
        var handleAsPointer = GCHandle.ToIntPtr(gcHandle);
        if (pointer == IntPtr.Zero) throw new NullReferenceException(nameof(pointer));
        ClassInjectorBase.GetInjectedData(pointer)->managedGcHandle = GCHandle.ToIntPtr(gcHandle);
    }


    public static bool IsTypeRegisteredInIl2Cpp<T>() where T : class
    {
        return IsTypeRegisteredInIl2Cpp(typeof(T));
    }

    public static bool IsTypeRegisteredInIl2Cpp(Type type)
    {
        var currentPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (currentPointer != IntPtr.Zero)
            return true;
        if (IsManagedTypeInjected(type)) return true;

        return false;
    }

    internal static bool IsManagedTypeInjected(Type type)
    {
        lock (InjectedTypes)
        {
            if (InjectedTypes.Contains(type.FullName))
                return true;
        }

        return false;
    }

    public static void RegisterTypeInIl2Cpp<T>() where T : class
    {
        RegisterTypeInIl2Cpp(typeof(T));
    }

    public static void RegisterTypeInIl2Cpp(Type type)
    {
        RegisterTypeInIl2Cpp(type, RegisterTypeOptions.Default);
    }

    public static void RegisterTypeInIl2Cpp<T>(RegisterTypeOptions options) where T : class
    {
        RegisterTypeInIl2Cpp(typeof(T), options);
    }

    public static void RegisterTypeInIl2Cpp(Type type, RegisterTypeOptions options)
    {
        t_RegistrationDepth++;
        try
        {
            RegisterTypeInIl2CppInternal(type, options);
        }
        catch (Exception exception)
        {
            if (type != null)
            {
                lock (InjectedTypes) InjectedTypes.Remove(type.FullName);
            }

            throw new Exception($"Injecting {type?.FullName} into il2cpp failed: {exception.Message}", exception);
        }
        finally
        {
            t_RegistrationDepth--;
        }
    }

    // Inside a registration the class is still being built, registering it on first use there would drop the caller's options
    [ThreadStatic]
    private static int t_RegistrationDepth;

    /// <summary>
    ///     Register a managed subclass where using it unregistered used to fail, so AddComponent and new work without an
    ///     explicit registration like on Mono. Generated wrappers whose class the game lacks are left alone
    /// </summary>
    /// <param name="type">Type whose class pointer is still unset</param>
    internal static void RegisterOnFirstUse(Type type)
    {
        if (t_RegistrationDepth > 0 || !type.IsClass || type.IsGenericType || !typeof(Il2CppObjectBase).IsAssignableFrom(type) || IsGeneratedType(type))
            return;

        try
        {
            RegisterTypeInIl2Cpp(type);
        }
        catch (Exception exception)
        {
            // Thrown from a class constructor the failure would stick to the type for the rest of the session
            Logger.Instance.LogWarning("{Type} could not be registered on first use, register it with ClassInjector: {Exception}", type, exception);
        }
    }

    private static bool IsGeneratedType(Type type)
    {
        if (type.IsDefined(typeof(OriginalNameAttribute), false) || type.IsDefined(typeof(ObfuscatedNameAttribute), false))
            return true;

        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (field.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) || field.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void RegisterTypeInIl2CppInternal(Type type, RegisterTypeOptions options)
    {
        var interfaces = options.Interfaces;
        if (interfaces == null)
        {
            var interfacesAttribute = type.GetCustomAttribute<Il2CppImplementsAttribute>();
            interfaces = interfacesAttribute?.Interfaces ??
                         options.InterfacesResolver?.Invoke(type) ?? DeclaredIl2CppInterfaces(type);
        }

        if (type == null)
            throw new ArgumentException("Type argument cannot be null");

        if (type.IsGenericType || type.IsGenericTypeDefinition)
            throw new ArgumentException($"Type {type} is generic and can't be used in il2cpp");

        var currentPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (currentPointer != IntPtr.Zero)
            return; //already registered in il2cpp

        var baseType = type.BaseType;
        if (baseType == null)
            throw new ArgumentException($"Class {type} does not inherit from a class registered in il2cpp");

        var baseClassPointer =
            UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(baseType));
        if (baseClassPointer == null)
        {
            RegisterTypeInIl2Cpp(baseType, new RegisterTypeOptions { LogSuccess = options.LogSuccess });
            baseClassPointer =
                UnityVersionHandler.Wrap((Il2CppClass*)Il2CppClassPointerStore.GetNativeClassPointer(baseType));
        }

        InjectorHelpers.Setup();

        // Initialize the vtable of all base types (Class::Init is recursive internally). Everything below reads the
        // base class as a built class, so this has to have happened rather than merely been asked for.
        InjectorHelpers.EnsureClassInitialized(baseClassPointer);

        if (baseClassPointer.ValueType || baseClassPointer.EnumType)
            throw new ArgumentException($"Base class {baseType} is value type and can't be inherited from");

        if (baseClassPointer.IsGeneric)
            throw new ArgumentException($"Base class {baseType} is generic and can't be inherited from");

        // il2cpp compiles calls and casts on a sealed class as direct, so game code never reaches the injected class through it
        if ((baseClassPointer.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_SEALED) != 0)
            Logger.Instance.LogWarning("Base class {BaseType} is sealed in il2cpp, game code will not see {Type} through it", baseType, type);

        if ((baseClassPointer.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_INTERFACE) != 0)
            throw new ArgumentException($"Base class {baseType} is an interface and can't be inherited from");

        if (interfaces.Any(i => (i.Flags & Il2CppClassAttributes.TYPE_ATTRIBUTE_INTERFACE) == 0))
            throw new ArgumentException($"Some of the interfaces in {interfaces} are not interfaces");

        lock (InjectedTypes)
        {
            if (!InjectedTypes.Add(type.FullName))
                throw new ArgumentException(
                    $"Type with FullName {type.FullName} is already injected. Don't inject the same type twice, or use a different namespace");
        }

        var interfaceFunctionCount = interfaces.Sum(i => i.MethodCount);
        var eligibleMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(IsMethodEligible).ToArray();
        var abstractMethods = eligibleMethods.Where(x => x.IsAbstract).ToArray();

        // The abstract methods get a vtable slot each, leaving them out of the allocation corrupts the heap past the class
        var classPointer = UnityVersionHandler.NewClass(baseClassPointer.VtableCount + interfaceFunctionCount + abstractMethods.Length);

        classPointer.Image = InjectorHelpers.InjectedImage.ImagePointer;
        classPointer.Parent = baseClassPointer.ClassPointer;
        classPointer.ElementClass = classPointer.Class = classPointer.CastClass = classPointer.ClassPointer;
        classPointer.NativeSize = -1;
        classPointer.ActualSize = classPointer.InstanceSize = baseClassPointer.InstanceSize;

        classPointer.Initialized = true;
        classPointer.InitializedAndNoError = true;
        classPointer.SizeInited = true;
        classPointer.HasFinalize = true;
        classPointer.IsVtableInitialized = true;

        classPointer.Name = Marshal.StringToCoTaskMemUTF8(type.Name);
        classPointer.Namespace = Marshal.StringToCoTaskMemUTF8(type.Namespace ?? string.Empty);

        classPointer.ThisArg.Type = classPointer.ByValArg.Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS;
        classPointer.ThisArg.ByRef = true;

        // The collector reads the descriptor from the object's own class. Without it an injected object is
        // allocated unscanned, so the il2cpp references its base class holds are freed while still in use.
        classPointer.GcDesc = baseClassPointer.GcDesc;
        classPointer.HasReferences = baseClassPointer.HasReferences;

        classPointer.Flags = baseClassPointer.Flags; // todo: adjust flags?

        // An injected abstract class leaves its abstract methods as null vtable slots, a derived injection only fills those
        // when the flag says the base is abstract
        if (type.IsAbstract)
            classPointer.Flags |= Il2CppClassAttributes.TYPE_ATTRIBUTE_ABSTRACT;
        else
            classPointer.Flags &= ~Il2CppClassAttributes.TYPE_ATTRIBUTE_ABSTRACT;

        var fieldsToInject = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(IsFieldEligible)
            .ToArray();
        classPointer.FieldCount = (ushort)fieldsToInject.Length;

        var il2cppFields =
            (Il2CppFieldInfo*)Marshal.AllocHGlobal(classPointer.FieldCount * UnityVersionHandler.FieldInfoSize());
        var fieldOffset = (int)classPointer.InstanceSize;
        for (var i = 0; i < classPointer.FieldCount; i++)
        {
            var fieldInfo = UnityVersionHandler.Wrap(il2cppFields + i * UnityVersionHandler.FieldInfoSize());
            fieldInfo.Name = Marshal.StringToCoTaskMemUTF8(fieldsToInject[i].Name);
            fieldInfo.Parent = classPointer.ClassPointer;
            fieldInfo.Offset = fieldOffset;

            var fieldType = fieldsToInject[i].FieldType == typeof(Il2CppStringField)
                ? typeof(string)
                : fieldsToInject[i].FieldType.GenericTypeArguments[0];
            var fieldAttributes = fieldsToInject[i].Attributes;
            var fieldInfoClass = Il2CppClassPointerStore.GetNativeClassPointer(fieldType);
            if (!_injectedFieldTypes.TryGetValue((fieldType, fieldAttributes), out var fieldTypePtr))
            {
                var classType =
                    UnityVersionHandler.Wrap((Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(fieldInfoClass));

                var duplicatedType = UnityVersionHandler.NewType();
                duplicatedType.Data = classType.Data;
                duplicatedType.Attrs = (ushort)fieldAttributes;
                duplicatedType.Type = classType.Type;
                duplicatedType.ByRef = classType.ByRef;
                duplicatedType.Pinned = classType.Pinned;

                _injectedFieldTypes[(fieldType, fieldAttributes)] = duplicatedType.Pointer;
                fieldTypePtr = duplicatedType.Pointer;
            }

            fieldInfo.Type = (Il2CppTypeStruct*)fieldTypePtr;
            if (fieldInfoClass == IntPtr.Zero)
                throw new Exception($"Type {fieldType} in {type}.{fieldsToInject[i].Name} doesn't exist in Il2Cpp");

            if (IL2CPP.il2cpp_class_is_valuetype(fieldInfoClass))
            {
                uint _align = 0;
                var fieldSize = IL2CPP.il2cpp_class_value_size(fieldInfoClass, ref _align);
                fieldOffset += fieldSize;
            }
            else
            {
                fieldOffset += sizeof(Il2CppObject*);
            }
        }

        classPointer.Fields = il2cppFields;

        classPointer.InstanceSize = (uint)(fieldOffset + sizeof(InjectedClassData));
        classPointer.ActualSize = classPointer.InstanceSize;

        var methodsOffset = type.IsAbstract ? 1 : 2; // 1 is the finalizer, 1 is empty ctor
        var methodCount = methodsOffset + eligibleMethods.Length;

        classPointer.MethodCount = (ushort)methodCount;
        var methodPointerArray = (Il2CppMethodInfo**)Marshal.AllocHGlobal(methodCount * IntPtr.Size);
        classPointer.Methods = methodPointerArray;

        methodPointerArray[0] = ConvertStaticMethod(FinalizeDelegate, "Finalize", classPointer);
        var finalizeMethod = UnityVersionHandler.Wrap(methodPointerArray[0]);
        var fieldsToInitialize = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(IsFieldEligible)
            .ToArray();

        if (!type.IsAbstract) methodPointerArray[1] = ConvertStaticMethod(CreateEmptyCtor(type, fieldsToInitialize), ".ctor", classPointer);
        var infos = new Dictionary<(string, int, bool), int>(eligibleMethods.Length);
        for (var i = 0; i < eligibleMethods.Length; i++)
        {
            var methodInfo = eligibleMethods[i];
            var methodInfoPointer = methodPointerArray[i + methodsOffset] = ConvertMethodInfo(methodInfo, classPointer);
            if (methodInfo.IsGenericMethod && !methodInfo.IsAbstract)
                InflatedMethodFromContextDictionary.TryAdd((IntPtr)methodInfoPointer, (methodInfo, new ConcurrentDictionary<IntPtr, IntPtr>()));
            infos[(methodInfo.Name, methodInfo.GetParameters().Length, methodInfo.IsGenericMethod)] = i + methodsOffset;
        }

        var vTablePointer = (VirtualInvokeData*)classPointer.VTable;
        var baseVTablePointer = (VirtualInvokeData*)baseClassPointer.VTable;
        classPointer.VtableCount = (ushort)(baseClassPointer.VtableCount + interfaceFunctionCount + abstractMethods.Length);

        var extendsAbstract = baseClassPointer.Flags.HasFlag(Il2CppClassAttributes.TYPE_ATTRIBUTE_ABSTRACT);
        var abstractBaseMethods = new List<INativeMethodInfoStruct>();

        if (extendsAbstract)
        {
            static void FindAbstractMethods(List<INativeMethodInfoStruct> list, INativeClassStruct klass)
            {
                if (klass.Parent != default) FindAbstractMethods(list, UnityVersionHandler.Wrap(klass.Parent));

                for (var i = 0; i < klass.MethodCount; i++)
                {
                    var baseMethod = UnityVersionHandler.Wrap(klass.Methods[i]);
                    var name = Marshal.PtrToStringUTF8(baseMethod.Name)!;

                    if (baseMethod.Flags.HasFlag(Il2CppMethodFlags.METHOD_ATTRIBUTE_ABSTRACT))
                    {
                        list.Add(baseMethod);
                    }
                    else
                    {
                        var existing = list.SingleOrDefault(m =>
                        {
                            if (Marshal.PtrToStringUTF8(m.Name) != name) return false;
                            if (m.ParametersCount != baseMethod.ParametersCount) return false;
                            if (GetIl2CppTypeFullName(m.ReturnType) != GetIl2CppTypeFullName(baseMethod.ReturnType)) return false;

                            for (var i = 0; i < m.ParametersCount; i++)
                            {
                                var parameterInfo = UnityVersionHandler.Wrap(baseMethod.Parameters, i);
                                var otherParameterInfo = UnityVersionHandler.Wrap(m.Parameters, i);

                                if (GetIl2CppTypeFullName(parameterInfo.ParameterType) != GetIl2CppTypeFullName(otherParameterInfo.ParameterType)) return false;
                            }

                            return true;
                        });

                        if (existing != null)
                        {
                            list.Remove(existing);
                        }
                    }
                }
            }

            FindAbstractMethods(abstractBaseMethods, baseClassPointer);
        }

        var abstractV = 0;

        INativeMethodInfoStruct HandleAbstractMethod(int position)
        {
            if (!extendsAbstract) throw new NullReferenceException("VTable method was null even though base type isn't abstract");

            if (abstractV >= abstractBaseMethods.Count) throw new Exception($"abstract slot {position} has no base method");

            var nativeMethodInfoStruct = abstractBaseMethods[abstractV++];

            vTablePointer[position].method = nativeMethodInfoStruct.MethodInfoPointer;
            vTablePointer[position].methodPtr = nativeMethodInfoStruct.MethodPointer;
            return nativeMethodInfoStruct;
        }

        var boundMethods = new HashSet<MethodInfo>();
        var unboundSlots = new Dictionary<string, string>();

        for (var i = 0; i < baseClassPointer.VtableCount; i++)
        {
            vTablePointer[i] = baseVTablePointer[i];

            INativeMethodInfoStruct baseMethod;

            if (baseVTablePointer[i].method == default)
            {
                baseMethod = HandleAbstractMethod(i);
            }
            else
            {
                baseMethod = UnityVersionHandler.Wrap(vTablePointer[i].method);
            }

            if (baseMethod.Name == IntPtr.Zero)
            {
                baseMethod = HandleAbstractMethod(i);
            }

            var methodName = Marshal.PtrToStringUTF8(baseMethod.Name);

            if (string.IsNullOrEmpty(methodName))
            {
                continue;
            }

            if (methodName == "Finalize") // slot number is not static
            {
                vTablePointer[i].method = methodPointerArray[0];
                vTablePointer[i].methodPtr = finalizeMethod.MethodPointer;
                continue;
            }

            var parameters = new Type[baseMethod.ParametersCount];

            for (var j = 0; j < baseMethod.ParametersCount; j++)
            {
                var parameterInfo = UnityVersionHandler.Wrap(baseMethod.Parameters, j);

                // Generic parameters and wrapped value types have no resolvable System.Type, the override is then matched by name
                try
                {
                    parameters[j] = SystemTypeFromIl2CppType(parameterInfo.ParameterType);
                }
                catch (Exception)
                {
                    parameters = null;
                    break;
                }
            }

            var monoMethodImplementation = FindBaseInterfaceImplementation(type, baseClassPointer, i, baseMethod.ParametersCount)
                ?? (parameters != null
                    ? type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, parameters)
                    : null)
                ?? FindOverrideByName(type, methodName, baseMethod, boundMethods);

            if (monoMethodImplementation != null && monoMethodImplementation.IsAbstract)
            {
                continue;
            }

            var methodPointerArrayIndex = Array.IndexOf(eligibleMethods, monoMethodImplementation);
            if (methodPointerArrayIndex >= 0)
            {
                var method = UnityVersionHandler.Wrap(methodPointerArray[methodPointerArrayIndex + methodsOffset]);

                // A generic method has no trampoline, the base entry stays because an empty slot would crash the call
                if (method.MethodPointer != IntPtr.Zero)
                {
                    vTablePointer[i].method = methodPointerArray[methodPointerArrayIndex + methodsOffset];
                    vTablePointer[i].methodPtr = method.MethodPointer;
                }
            }

            if (monoMethodImplementation == null)
            {
                unboundSlots.TryAdd(methodName, DescribeSignature(methodName, baseMethod));
            }
            else
            {
                boundMethods.Add(monoMethodImplementation);
            }

            if (vTablePointer[i].method == default || vTablePointer[i].methodPtr == IntPtr.Zero)
            {
                throw new Exception($"No method found for vtable entry {methodName}({DescribeParameters(baseMethod)})");
            }
        }

        var offsets = new int[interfaces.Count];

        var index = baseClassPointer.VtableCount;
        for (var i = 0; i < interfaces.Count; i++)
        {
            offsets[i] = index;
            var managedInterface = interfaces.ManagedTypes.TryGetValue((IntPtr)interfaces[i].ClassPointer, out var knownInterface) ? knownInterface : null;
            InterfaceMapping? mapping = managedInterface != null && managedInterface.IsAssignableFrom(type) ? type.GetInterfaceMap(managedInterface) : null;
            for (var j = 0; j < interfaces[i].MethodCount; j++)
            {
                var vTableMethod = UnityVersionHandler.Wrap(interfaces[i].Methods[j]);
                var methodName = Marshal.PtrToStringUTF8(vTableMethod.Name);
                var methodIndex = FindInterfaceImplementation(mapping, methodName, vTableMethod.ParametersCount, vTableMethod.IsGeneric, eligibleMethods, methodsOffset, infos);
                if (methodIndex < 0)
                {
                    var inherited = FindBaseClassMethod(baseClassPointer, methodName, vTableMethod.ParametersCount);
                    if (inherited != null)
                    {
                        vTablePointer[index].method = inherited.MethodInfoPointer;
                        vTablePointer[index].methodPtr = inherited.MethodPointer;
                        ++index;
                        continue;
                    }

                    Logger.Instance.LogWarning(
                        "Type {Type} does not implement {Interface}.{Method} of il2cpp, a call from il2cpp to it will crash. Declare `{Signature}`{NearMisses}",
                        type, Marshal.PtrToStringUTF8(interfaces[i].Name), methodName, DescribeSignature(methodName, vTableMethod),
                        DescribeNearMisses(type, methodName));
                    ++index;
                    continue;
                }

                var method = methodPointerArray[methodIndex];
                vTablePointer[index].method = method;
                vTablePointer[index].methodPtr = UnityVersionHandler.Wrap(method).MethodPointer;
                if (methodIndex - methodsOffset >= 0 && methodIndex - methodsOffset < eligibleMethods.Length)
                {
                    boundMethods.Add(eligibleMethods[methodIndex - methodsOffset]);
                }

                ++index;
            }
        }

        ReportUnboundOverrides(type, boundMethods, unboundSlots);

        classPointer.InheritInterfaces(baseClassPointer, interfaces, offsets);

        for (var i = 0; i < abstractMethods.Length; i++)
        {
            vTablePointer[index++] = default;
        }

        var TypeHierarchyDepth = 1 + baseClassPointer.TypeHierarchyDepth;
        classPointer.TypeHierarchyDepth = (byte)TypeHierarchyDepth;
        classPointer.TypeHierarchy = (Il2CppClass**)Marshal.AllocHGlobal(TypeHierarchyDepth * IntPtr.Size);
        for (var i = 0; i < TypeHierarchyDepth; i++)
            classPointer.TypeHierarchy[i] = baseClassPointer.TypeHierarchy[i];
        classPointer.TypeHierarchy[TypeHierarchyDepth - 1] = classPointer.ClassPointer;

        classPointer.ByValArg.Data =
            classPointer.ThisArg.Data = (IntPtr)InjectorHelpers.CreateClassToken(classPointer.Pointer);

        RuntimeSpecificsStore.SetClassInfo(classPointer.Pointer, true);
        Il2CppClassPointerStore.SetNativeClassPointer(type, classPointer.Pointer);

        InjectorHelpers.AddTypeToLookup(type, classPointer.Pointer);
        InjectedScriptNames.EnsurePatched();
        CustomAttributeSupport.Register(type, classPointer.Pointer);

        if (options.LogSuccess)
            Logger.Instance.LogInformation("Registered mono type {Type} in il2cpp domain", type);
    }

    private static bool IsTypeSupported(Type type)
    {
        if (type == typeof(string) || type == typeof(void) || type.IsGenericParameter) return true;
        if (type.IsValueType) return type.IsPrimitive || IsIl2CppBacked(type);
        if (type.IsByRef) return IsTypeSupported(type.GetElementType());
        if (type.IsInterface) return IsIl2CppInterface(type);

        // An il2cpp object the trampoline has to rebuild needs the pointer constructor
        return typeof(Il2CppObjectBase).IsAssignableFrom(type) && HasPointerConstructor(type);
    }

    private static bool HasPointerConstructor(Type type)
    {
        return type.GetConstructors().Any(it =>
        {
            var parameters = it.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr);
        });
    }

    // Il2cpp parameter types that System.Type cannot express, a generic parameter or a wrapped value type such as Nullable<T>,
    // leave the signature lookup no way to match. The override is found by name and arity, compared by il2cpp class when ambiguous.
    private static MethodInfo? FindOverrideByName(Type type, string methodName, INativeMethodInfoStruct baseMethod, HashSet<MethodInfo> boundMethods)
    {
        var candidates = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(it => it.Name == methodName && it.GetParameters().Length == baseMethod.ParametersCount
                         && it.IsGenericMethod == baseMethod.IsGeneric)
            .ToArray();

        var baseClasses = new IntPtr[baseMethod.ParametersCount];
        for (var i = 0; i < baseMethod.ParametersCount; i++)
        {
            // A parameter of an injected type has no class to compare against, the name match then decides on its own
            try
            {
                baseClasses[i] = IL2CPP.il2cpp_class_from_il2cpp_type((IntPtr)UnityVersionHandler.Wrap(baseMethod.Parameters, i).ParameterType);
            }
            catch (Exception)
            {
                baseClasses[i] = IntPtr.Zero;
            }
        }

        // Several slots can share a name and a parameter count, so a method only goes into more than one of them
        // when every parameter was compared. Otherwise it takes the first and the rest keep the base implementation.
        var confirmed = Array.TrueForAll(baseClasses, it => it != IntPtr.Zero);
        var matches = candidates.Where(it => ParametersMatch(it, baseClasses)).ToArray();
        if (matches.Length == 1)
        {
            return confirmed || !boundMethods.Contains(matches[0]) ? matches[0] : null;
        }

        if (matches.Length > 1 || candidates.Length != 1 || boundMethods.Contains(candidates[0]))
        {
            return null;
        }

        // The il2cpp classes did not confirm the one candidate, binding it is still better than an empty slot
        Logger.Instance.LogDebug("Bound {Method} of {Type} to the il2cpp slot by name, its parameters could not be confirmed",
            candidates[0], type);
        return candidates[0];
    }

    // A method that matches the name of an il2cpp slot but not its signature is silently never called,
    // which is the easiest mistake to make when porting a type, so it is reported with the signature to write.
    private static void ReportUnboundOverrides(Type type, HashSet<MethodInfo> boundMethods, Dictionary<string, string> unboundSlots)
    {
        foreach (var declared in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .GroupBy(it => it.Name))
        {
            if (!unboundSlots.TryGetValue(declared.Key, out var signature) || declared.Any(boundMethods.Contains))
            {
                continue;
            }

            Logger.Instance.LogWarning(
                "Type {Type} declares {Method} but no overload matches an il2cpp method, so the game keeps calling the base one. Declare `{Signature}`{NearMisses}",
                type, declared.Key, signature, DescribeNearMisses(type, declared.Key));
        }
    }

    // The C# declaration an injected type needs for an il2cpp slot, so a mismatch says what to write instead.
    // Nothing here may throw, it only runs to explain a problem that already happened.
    private static string DescribeSignature(string methodName, INativeMethodInfoStruct method)
    {
        try
        {
            var parameters = new string[method.ParametersCount];
            for (var i = 0; i < method.ParametersCount; i++)
            {
                parameters[i] = $"{DescribeManagedType(UnityVersionHandler.Wrap(method.Parameters, i).ParameterType)} arg{i}";
            }

            return $"public {DescribeManagedType(method.ReturnType)} {methodName}({string.Join(", ", parameters)})";
        }
        catch (Exception)
        {
            return methodName;
        }
    }

    private static string DescribeNearMisses(Type type, string methodName)
    {
        var declared = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(it => it.Name == methodName)
            .Select(it => $"{it.ReturnType.Name} {it.Name}({string.Join(", ", it.GetParameters().Select(p => p.ParameterType.Name))})")
            .ToArray();

        return declared.Length == 0 ? string.Empty : $", found {string.Join(" and ", declared)}";
    }

    // Generated code names a game type as it is and a framework type under Il2Cpp, which is what the hint has to show
    private static string DescribeManagedType(Il2CppTypeStruct* type)
    {
        try
        {
            var fullName = GetIl2CppTypeFullName(type).Split(',')[0];
            return Type.GetType("Il2Cpp" + fullName) != null ? "Il2Cpp" + fullName : fullName;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string DescribeParameters(INativeMethodInfoStruct method)
    {
        try
        {
            var names = new string[method.ParametersCount];
            for (var i = 0; i < method.ParametersCount; i++)
            {
                names[i] = GetIl2CppTypeFullName(UnityVersionHandler.Wrap(method.Parameters, i).ParameterType).Split(',')[0];
            }

            return string.Join(", ", names);
        }
        catch (Exception)
        {
            return $"{method.ParametersCount} parameters";
        }
    }

    private static bool ParametersMatch(MethodInfo candidate, IntPtr[] baseClasses)
    {
        var parameters = candidate.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            IntPtr candidateClass;
            try
            {
                candidateClass = Il2CppClassPointerStore.GetNativeClassPointer(parameters[i].ParameterType);
            }
            catch (Exception)
            {
                continue;
            }

            // A type neither side can name cannot rule the candidate out, the parameters that do have a class decide
            if (candidateClass == IntPtr.Zero || baseClasses[i] == IntPtr.Zero) continue;

            if (candidateClass != baseClasses[i])
            {
                return false;
            }
        }

        return true;
    }

    // A value type declared in a plugin assembly has no il2cpp class, converting a method that uses it would crash on the null pointer
    private static bool IsIl2CppBacked(Type type)
    {
        try
        {
            return Il2CppClassPointerStore.GetNativeClassPointer(type) != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsIl2CppInterface(Type type)
    {
        return type.IsInterface && !type.ContainsGenericParameters && Il2CppClassPointerStore.GetNativeClassPointer(type) != IntPtr.Zero;
    }

    // Interfaces the type adds on top of its base class, the base's own are already in the il2cpp class it derives from
    private static Type[] DeclaredIl2CppInterfaces(Type type)
    {
        var inherited = type.BaseType?.GetInterfaces() ?? Array.Empty<Type>();
        return type.GetInterfaces().Where(it => !inherited.Contains(it) && IsIl2CppInterface(it)).ToArray();
    }

    // Base slots inside an interface block resolve through the managed interface map, which knows explicit implementations.
    // The caller falls back to a plain name lookup, so a slot this cannot serve has to come back as null rather than throw.
    private static MethodInfo? FindBaseInterfaceImplementation(Type type, INativeClassStruct baseClass, int slot, int parameterCount)
    {
        try
        {
            for (var i = 0; i < baseClass.InterfaceOffsetsCount; i++)
            {
                var pair = new Il2CppRuntimeInterfaceOffsetPair
                {
                    interfaceType = baseClass.DispatchInterface(i),
                    offset = baseClass.DispatchInterfaceOffset(i),
                };

                // A pair that cannot be sized cannot be ruled in or out, and a later pair may still own the slot
                var interfaceClass = UnityVersionHandler.Wrap(pair.interfaceType);
                if (interfaceClass == null)
                    continue;

                if (slot < pair.offset || slot >= pair.offset + interfaceClass.MethodCount)
                    continue;

                // Past here this pair owns the slot, so no other one can: failing is the answer, not a reason to keep looking
                Type managedInterface;
                try
                {
                    managedInterface = SystemTypeFromIl2CppType((Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type((IntPtr)pair.interfaceType));
                }
                catch (Exception)
                {
                    // An il2cpp interface with no generated proxy is an ordinary miss, not a failure
                    return null;
                }

                if (!managedInterface.IsInterface || !managedInterface.IsAssignableFrom(type))
                    return null;

                // The base slot may carry a dotted explicit name while the interface method has the plain one.
                // ClassInit first, the interface's method table may not be built yet.
                InjectorHelpers.ClassInit(pair.interfaceType);

                var methods = interfaceClass.Methods;
                var methodIndex = slot - pair.offset;
                if (methods == null || methodIndex >= interfaceClass.MethodCount)
                    return null;

                var vTableMethod = UnityVersionHandler.Wrap(methods[methodIndex]);
                if (vTableMethod == null)
                    return null;

                var interfaceMethodName = Marshal.PtrToStringUTF8(vTableMethod.Name);
                var map = type.GetInterfaceMap(managedInterface);
                for (var j = 0; j < map.InterfaceMethods.Length; j++)
                {
                    var interfaceMethod = map.InterfaceMethods[j];
                    if (interfaceMethod.Name != interfaceMethodName || interfaceMethod.GetParameters().Length != parameterCount)
                        continue;

                    // An abstract type can leave an interface slot unimplemented, and the map holds null for it
                    var target = map.TargetMethods[j];
                    return target?.DeclaringType == type ? target : null;
                }

                return null;
            }
        }
        catch (Exception exception)
        {
            Logger.Instance.LogWarning(exception, "Interface map lookup for vtable slot {Slot} of {Type} failed, falling back to the name lookup", slot, type);
        }

        return null;
    }

    // Explicit implementations carry the interface name in the method name, so the runtime's mapping is asked first.
    // The name lookup covers interfaces handed over as raw class pointers.
    private static int FindInterfaceImplementation(InterfaceMapping? mapping, string methodName, int parameterCount, bool isGeneric,
        MethodInfo[] eligibleMethods, int methodsOffset, Dictionary<(string, int, bool), int> infos)
    {
        if (mapping.HasValue)
        {
            var map = mapping.Value;
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var interfaceMethod = map.InterfaceMethods[i];
                if (interfaceMethod.Name != methodName || interfaceMethod.GetParameters().Length != parameterCount || interfaceMethod.IsGenericMethod != isGeneric)
                    continue;

                var target = map.TargetMethods[i];
                if (target.DeclaringType!.IsInterface)
                    break;

                var eligibleIndex = Array.IndexOf(eligibleMethods, target);
                return eligibleIndex < 0 ? -1 : eligibleIndex + methodsOffset;
            }
        }

        return infos.TryGetValue((methodName, parameterCount, isGeneric), out var methodIndex) ? methodIndex : -1;
    }

    // An interface method implemented by a base class is not in this type's own method table, the base class carries it
    private static INativeMethodInfoStruct FindBaseClassMethod(INativeClassStruct klass, string methodName, int parameterCount)
    {
        var current = klass;
        while (current != null)
        {
            for (var i = 0; i < current.MethodCount; i++)
            {
                var method = UnityVersionHandler.Wrap(current.Methods[i]);
                if (method.MethodPointer == IntPtr.Zero || method.ParametersCount != parameterCount)
                    continue;

                if (Marshal.PtrToStringUTF8(method.Name) == methodName)
                    return method;
            }

            current = current.Parent != default ? UnityVersionHandler.Wrap(current.Parent) : null;
        }

        return null;
    }

    private static bool IsFieldEligible(FieldInfo field)
    {
        if (!field.FieldType.IsGenericType) return field.FieldType == typeof(Il2CppStringField);
        var genericTypeDef = field.FieldType.GetGenericTypeDefinition();
        if (genericTypeDef != typeof(Il2CppReferenceField<>) && genericTypeDef != typeof(Il2CppValueField<>))
            return false;

        return IsTypeSupported(field.FieldType.GenericTypeArguments[0]);
    }

    private static bool IsMethodEligible(MethodInfo method)
    {
        if (method.Name == "Finalize") return false;
        if (method.IsStatic) return false;
        if (method.CustomAttributes.Any(it => typeof(HideFromIl2CppAttribute).IsAssignableFrom(it.AttributeType)))
            return false;

        if (method.DeclaringType != null)
        {
            if (method.DeclaringType.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(property => property.GetAccessors(true).Contains(method))
                .Any(property =>
                    property.CustomAttributes.Any(it =>
                        typeof(HideFromIl2CppAttribute).IsAssignableFrom(it.AttributeType)))
               )
                return false;

            foreach (var eventInfo in method.DeclaringType.GetEvents(BindingFlags.Instance | BindingFlags.Public |
                                                                     BindingFlags.NonPublic |
                                                                     BindingFlags.DeclaredOnly))
                if ((eventInfo.GetAddMethod(true) == method || eventInfo.GetRemoveMethod(true) == method) &&
                    eventInfo.GetCustomAttribute<HideFromIl2CppAttribute>() != null)
                    return false;
        }

        if (!IsTypeSupported(method.ReturnType))
        {
            ReportIneligible(method, $"return type {method.ReturnType}", method.ReturnType);
            return false;
        }

        foreach (var parameter in method.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            if (!IsTypeSupported(parameterType))
            {
                ReportIneligible(method, $"parameter {parameter} of type {parameterType}", parameterType);
                return false;
            }
        }

        return true;
    }

    // A method il2cpp has no types for stays invisible to it, which costs nothing while only managed code calls it.
    // The case worth a warning is the one the game was meant to call, where it goes on calling the base instead.
    private static void ReportIneligible(MethodInfo method, string reason, Type unsupported)
    {
        var suggestion = SuggestIl2CppType(unsupported);
        var hint = suggestion != null ? $", declare it as {suggestion}" : "";

        if (IsCalledByIl2Cpp(method))
        {
            Logger.Instance.LogWarning(
                "Method {Method} on type {DeclaringType} overrides an il2cpp method but has an unsupported {Reason}, so the game keeps calling the base one{Hint}",
                method.ToString(), method.DeclaringType, reason, hint);
            return;
        }

        Logger.Instance.LogDebug(
            "Method {Method} on type {DeclaringType} has an unsupported {Reason}, only managed code can call it{Hint}",
            method.ToString(), method.DeclaringType, reason, hint);
    }

    private static bool IsCalledByIl2Cpp(MethodInfo method)
    {
        if (method.DeclaringType == null) return false;

        var declaring = method.GetBaseDefinition().DeclaringType;
        if (declaring != null && declaring != method.DeclaringType && IsRealIl2CppType(declaring)) return true;

        foreach (var @interface in method.DeclaringType.GetInterfaces())
            if (IsRealIl2CppType(@interface) && @interface.GetMethod(method.Name) != null)
                return true;

        return false;
    }

    // Both sides of an injected base are managed, so a call between them never goes through il2cpp and the
    // signature it cannot represent costs nothing
    private static bool IsRealIl2CppType(Type type)
    {
        var pointer = SafeClassPointer(type);
        return pointer != IntPtr.Zero && !RuntimeSpecificsStore.IsInjected(pointer);
    }

    private static IntPtr SafeClassPointer(Type type)
    {
        try
        {
            return Il2CppClassPointerStore.GetNativeClassPointer(type);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    // The type the method has to use instead, when the interop assemblies carry one
    private static string? SuggestIl2CppType(Type type)
    {
        try
        {
            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                if (element == typeof(string)) return "Il2CppStringArray";
                return element.IsPrimitive || element.IsEnum
                    ? $"Il2CppStructArray<{element.Name}>"
                    : $"Il2CppReferenceArray<{element.Name}>";
            }

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            var arity = definition.IsGenericType ? definition.GetGenericArguments().Length : 0;
            var name = "Il2Cpp" + definition.FullName!.Split('`')[0];
            var qualified = arity > 0 ? $"{name}`{arity}" : name;
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType(qualified) != null)) return null;

            return arity > 0
                ? $"{name}<{string.Join(", ", type.GetGenericArguments().Select(argument => argument.Name))}>"
                : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Il2CppMethodInfo* ConvertStaticMethod(VoidCtorDelegate voidCtor, string methodName,
        INativeClassStruct declaringClass)
    {
        var converted = UnityVersionHandler.NewMethod();
        converted.Name = Marshal.StringToCoTaskMemUTF8(methodName);
        converted.Class = declaringClass.ClassPointer;

        Delegate invoker;
        if (UnityVersionHandler.IsMetadataV29OrHigher)
        {
            invoker = new InvokerDelegateMetadataV29(StaticVoidIntPtrInvoker_MetadataV29);
        }
        else
        {
            invoker = new InvokerDelegate(StaticVoidIntPtrInvoker);
        }

        GCHandle.Alloc(invoker);
        converted.InvokerMethod = Marshal.GetFunctionPointerForDelegate(invoker);

        converted.MethodPointer = Marshal.GetFunctionPointerForDelegate(voidCtor);
        converted.Slot = ushort.MaxValue;
        converted.ReturnType =
            (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(Il2CppClassPointerStore<Void>.NativeClassPtr);

        converted.Flags = Il2CppMethodFlags.METHOD_ATTRIBUTE_PUBLIC |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_HIDE_BY_SIG |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_SPECIAL_NAME |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_RT_SPECIAL_NAME;

        return converted.MethodInfoPointer;
    }

    internal static Il2CppMethodInfo* ConvertMethodInfo(MethodInfo monoMethod, INativeClassStruct declaringClass)
    {
        var converted = UnityVersionHandler.NewMethod();
        converted.Name = Marshal.StringToCoTaskMemUTF8(monoMethod.Name);
        converted.Class = declaringClass.ClassPointer;

        var parameters = monoMethod.GetParameters();
        if (parameters.Length > 0)
        {
            converted.ParametersCount = (byte)parameters.Length;
            var paramsArray = UnityVersionHandler.NewMethodParameterArray(parameters.Length);
            converted.Parameters = paramsArray[0];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterInfo = parameters[i];
                var param = UnityVersionHandler.Wrap(paramsArray[i]);
                if (UnityVersionHandler.ParameterInfoHasNamePosToken())
                {
                    param.Name = Marshal.StringToCoTaskMemUTF8(parameterInfo.Name);
                    param.Position = i;
                    param.Token = 0;
                }

                var parameterType = parameterInfo.ParameterType;
                if (!parameterType.IsGenericParameter)
                {
                    if (parameterType.IsByRef)
                    {
                        var elementType = parameterType.GetElementType();
                        if (!elementType.IsGenericParameter)
                        {
                            var elemType = UnityVersionHandler.Wrap(
                                (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                                    Il2CppClassPointerStore.GetNativeClassPointer(elementType)));
                            var refType = UnityVersionHandler.NewType();
                            refType.Data = elemType.Data;
                            refType.Attrs = elemType.Attrs;
                            refType.Type = elemType.Type;
                            refType.ByRef = true;
                            refType.Pinned = elemType.Pinned;
                            param.ParameterType = refType.TypePointer;
                        }
                        else
                        {
                            var type = UnityVersionHandler.NewType();
                            type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
                            type.ByRef = true;
                            param.ParameterType = type.TypePointer;
                        }
                    }
                    else
                    {
                        param.ParameterType =
                            (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                                Il2CppClassPointerStore.GetNativeClassPointer(parameterType));
                    }
                }
                else
                {
                    var type = UnityVersionHandler.NewType();
                    type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
                    param.ParameterType = type.TypePointer;
                }
            }
        }

        if (monoMethod.IsGenericMethod)
        {
            if (monoMethod.ContainsGenericParameters)
                converted.IsGeneric = true;
            else
                converted.IsInflated = true;
        }

        if (!monoMethod.ContainsGenericParameters && !monoMethod.IsAbstract)
        {
            converted.InvokerMethod = Marshal.GetFunctionPointerForDelegate(GetOrCreateInvoker(monoMethod));
            converted.MethodPointer = Marshal.GetFunctionPointerForDelegate(GetOrCreateTrampoline(monoMethod));
            converted.VirtualMethodPointer = converted.MethodPointer;
        }

        converted.Slot = ushort.MaxValue;

        if (!monoMethod.ReturnType.IsGenericParameter)
        {
            converted.ReturnType =
                (Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type(
                    Il2CppClassPointerStore.GetNativeClassPointer(monoMethod.ReturnType));
        }
        else
        {
            var type = UnityVersionHandler.NewType();
            type.Type = Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
            converted.ReturnType = type.TypePointer;
        }

        converted.Flags = Il2CppMethodFlags.METHOD_ATTRIBUTE_PUBLIC |
                          Il2CppMethodFlags.METHOD_ATTRIBUTE_HIDE_BY_SIG;

        if (monoMethod.IsAbstract)
        {
            converted.Flags |= Il2CppMethodFlags.METHOD_ATTRIBUTE_ABSTRACT;
        }

        return converted.MethodInfoPointer;
    }

    private static VoidCtorDelegate CreateEmptyCtor(Type targetType, FieldInfo[] fieldsToInitialize)
    {
        var method = new DynamicMethod("FromIl2CppCtorDelegate", MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard, typeof(void), new[] { typeof(IntPtr) }, targetType, true);

        var body = method.GetILGenerator();

        var monoCtor = targetType.GetConstructor(new[] { typeof(IntPtr) });
        if (monoCtor != null)
        {
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Newobj, monoCtor);
        }
        else
        {
            var local = body.DeclareLocal(targetType);
            body.Emit(OpCodes.Ldtoken, targetType);
            body.Emit(OpCodes.Call,
                typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), BindingFlags.Public | BindingFlags.Static)!);
            body.Emit(OpCodes.Call,
                typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject),
                    BindingFlags.Public | BindingFlags.Static)!);
            body.Emit(OpCodes.Stloc, local);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Call,
                typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.CreateGCHandle),
                    BindingFlags.NonPublic | BindingFlags.Instance)!);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Ldc_I4_1);
            body.Emit(OpCodes.Stfld,
                typeof(Il2CppObjectBase).GetField(nameof(Il2CppObjectBase.isWrapped),
                    BindingFlags.NonPublic | BindingFlags.Instance)!);
            body.Emit(OpCodes.Ldloc, local);
            body.Emit(OpCodes.Call,
                targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    Type.EmptyTypes, Array.Empty<ParameterModifier>())!);
            body.Emit(OpCodes.Ldloc, local);
        }

        foreach (var field in fieldsToInitialize)
        {
            body.Emit(OpCodes.Dup);
            body.Emit(OpCodes.Dup);
            body.Emit(OpCodes.Ldstr, field.Name);
            body.Emit(OpCodes.Newobj, field.FieldType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Il2CppObjectBase), typeof(string) }, Array.Empty<ParameterModifier>())
            );
            body.Emit(OpCodes.Stfld, field);
        }

        body.Emit(OpCodes.Dup);
        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(RunNativeBaseConstructor))!);
        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(ProcessNewObject))!);

        body.Emit(OpCodes.Ret);

        var @delegate = (VoidCtorDelegate)method.CreateDelegate(typeof(VoidCtorDelegate));
        GCHandle.Alloc(@delegate); // pin it forever
        return @delegate;
    }

    // il2cpp code that creates an injected type (new T() in a game factory) only reaches the empty constructor,
    // so the game base constructor C# would chain to has to run here or the base fields stay uninitialized
    public static void RunNativeBaseConstructor(Il2CppObjectBase obj)
    {
        var constructor = NativeBaseConstructors.GetOrAdd(obj.GetType(), FindNativeBaseConstructor);
        if (constructor == IntPtr.Zero)
            return;

        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(constructor, obj.Pointer, (void**)IntPtr.Zero, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
    }

    private static readonly ConcurrentDictionary<Type, IntPtr> NativeBaseConstructors = new();

    private static IntPtr FindNativeBaseConstructor(Type injectedType)
    {
        var type = NativeBaseType(injectedType);
        if (type == null || type == typeof(Il2CppObjectBase) || type == typeof(Il2CppSystem.Object))
            return IntPtr.Zero;

        var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (classPointer == IntPtr.Zero)
            return IntPtr.Zero;
        var constructor = IL2CPP.il2cpp_class_get_method_from_name(classPointer, ".ctor", 0);
        if (constructor == IntPtr.Zero)
            Logger.Instance.LogTrace("{Type} has no parameterless il2cpp constructor to run for {Injected}", type, injectedType);
        return constructor;
    }

    private static Type? NativeBaseType(Type injectedType)
    {
        var type = injectedType.BaseType;
        while (type != null && IsManagedTypeInjected(type))
            type = type.BaseType;
        return type;
    }

    public static void Finalize(IntPtr ptr)
    {
        // The il2cpp GC can reach an injected object that never got a managed handle, and an exception raised on
        // the finalizer thread has nobody to catch it and takes the process down
        var gcHandle = ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr);
        if (gcHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            GCHandle.FromIntPtr(gcHandle).Free();
        }
        catch (Exception exception)
        {
            Logger.Instance.LogWarning("Finalizing an injected object failed: {Message}", exception.Message);
        }
    }

    private static Delegate GetOrCreateInvoker(MethodInfo monoMethod)
    {
        return InvokerCache.GetOrAdd(ExtractSignature(monoMethod),
            static (_, monoMethodInner) => CreateInvoker(monoMethodInner), monoMethod);
    }

    private static Delegate GetOrCreateTrampoline(MethodInfo monoMethod)
    {
        return CreateTrampoline(monoMethod);
    }

    private static Delegate CreateInvoker(MethodInfo monoMethod)
    {
        DynamicMethod method;
        if (UnityVersionHandler.IsMetadataV29OrHigher)
        {
            var parameterTypes = new[] { typeof(IntPtr), typeof(Il2CppMethodInfo*), typeof(IntPtr), typeof(IntPtr*), typeof(IntPtr*) };

            method = new DynamicMethod("Invoker_" + ExtractSignature(monoMethod),
                MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard, typeof(void),
                parameterTypes, monoMethod.DeclaringType, true);
        }
        else
        {
            var parameterTypes = new[] { typeof(IntPtr), typeof(Il2CppMethodInfo*), typeof(IntPtr), typeof(IntPtr*) };

            method = new DynamicMethod("Invoker_" + ExtractSignature(monoMethod),
                MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard, typeof(IntPtr),
                parameterTypes, monoMethod.DeclaringType, true);
        }

        var body = method.GetILGenerator();

        // A buffered struct return receives the il2cpp return storage as the first argument, ahead of this
        var returnsBuffer = TrampolineHelpers.NeedsReturnBuffer(monoMethod.ReturnType);
        LocalBuilder returnBuffer = null;
        if (returnsBuffer)
        {
            if (UnityVersionHandler.IsMetadataV29OrHigher)
            {
                body.Emit(OpCodes.Ldarg_S, (byte)4);
            }
            else
            {
                returnBuffer = body.DeclareLocal(typeof(IntPtr));
                body.Emit(OpCodes.Ldc_I4, TrampolineHelpers.ValueSize(monoMethod.ReturnType));
                body.Emit(OpCodes.Conv_U);
                body.Emit(OpCodes.Localloc);
                body.Emit(OpCodes.Stloc, returnBuffer);
                body.Emit(OpCodes.Ldloc, returnBuffer);
            }
        }

        body.Emit(OpCodes.Ldarg_2);
        for (var i = 0; i < monoMethod.GetParameters().Length; i++)
        {
            var parameterInfo = monoMethod.GetParameters()[i];
            body.Emit(OpCodes.Ldarg_3);
            body.Emit(OpCodes.Ldc_I4, i * IntPtr.Size);
            body.Emit(OpCodes.Add_Ovf_Un);
            var nativeType = parameterInfo.ParameterType.NativeType();
            body.Emit(OpCodes.Ldobj, typeof(IntPtr));
            if (nativeType != typeof(IntPtr))
                body.Emit(OpCodes.Ldobj, nativeType);
        }

        var nativeReturnType = returnsBuffer ? typeof(IntPtr) : monoMethod.ReturnType.NativeType();
        var calliParameters = (returnsBuffer ? new[] { typeof(IntPtr), typeof(IntPtr) } : new[] { typeof(IntPtr) })
            .Concat(monoMethod.GetParameters().Select(it => it.ParameterType.NativeType())).ToArray();
        body.Emit(OpCodes.Ldarg_0);
        body.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, nativeReturnType, calliParameters);

        if (UnityVersionHandler.IsMetadataV29OrHigher)
        {
            if (returnsBuffer)
            {
                body.Emit(OpCodes.Pop);
            }
            else if (monoMethod.ReturnType != typeof(void))
            {
                var returnValue = body.DeclareLocal(nativeReturnType);
                body.Emit(OpCodes.Stloc, returnValue);
                body.Emit(OpCodes.Ldarg_S, (byte)4);
                body.Emit(OpCodes.Ldloc, returnValue);
                body.Emit(OpCodes.Stobj, returnValue.LocalType);
            }
        }
        else
        {
            if (monoMethod.ReturnType == typeof(void))
            {
                body.Emit(OpCodes.Ldc_I4_0);
                body.Emit(OpCodes.Conv_I);
            }
            else if (returnsBuffer)
            {
                body.Emit(OpCodes.Pop);
                body.Emit(OpCodes.Ldsfld, ClassPointerField(monoMethod.ReturnType));
                body.Emit(OpCodes.Ldloc, returnBuffer);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_value_box))!);
            }
            else if (monoMethod.ReturnType.IsValueType || TrampolineHelpers.IsPassedByValue(monoMethod.ReturnType))
            {
                var returnValue = body.DeclareLocal(nativeReturnType);
                body.Emit(OpCodes.Stloc, returnValue);
                body.Emit(OpCodes.Ldsfld, ClassPointerField(monoMethod.ReturnType));
                body.Emit(OpCodes.Ldloca, returnValue);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_value_box))!);
            }
        }

        body.Emit(OpCodes.Ret);

        GCHandle.Alloc(method);

        var @delegate = method.CreateDelegate(GetInvokerDelegateType());
        GCHandle.Alloc(@delegate);
        return @delegate;
    }

    public static void GuardStack(string method)
    {
        if (System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return;
        }

        throw new InsufficientExecutionStackException(
            $"Injected method {method} ran out of stack. It is most likely recursing into itself, which kills the process. Stack:{Environment.NewLine}{new StackTrace(false)}");
    }

    private static FieldInfo ClassPointerField(Type type)
    {
        return typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr))!;
    }

    private static Type GetInvokerDelegateType()
    {
        if (UnityVersionHandler.IsMetadataV29OrHigher)
        {
            return typeof(InvokerDelegateMetadataV29);
        }

        return typeof(InvokerDelegate);
    }

    private static IntPtr StaticVoidIntPtrInvoker(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj,
        IntPtr* args)
    {
        CtorDelegates.GetOrAdd(methodPointer, Marshal.GetDelegateForFunctionPointer<VoidCtorDelegate>)(obj);
        return IntPtr.Zero;
    }

    private static void StaticVoidIntPtrInvoker_MetadataV29(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj,
        IntPtr* args, IntPtr* returnValue)
    {
        CtorDelegates.GetOrAdd(methodPointer, Marshal.GetDelegateForFunctionPointer<VoidCtorDelegate>)(obj);
    }

    private static readonly ConcurrentDictionary<IntPtr, VoidCtorDelegate> CtorDelegates = new();

    private static Delegate CreateTrampoline(MethodInfo monoMethod)
    {
        var returnsBuffer = TrampolineHelpers.NeedsReturnBuffer(monoMethod.ReturnType);
        var argumentOffset = returnsBuffer ? 1 : 0;
        var nativeParameterTypes = (returnsBuffer ? new[] { typeof(IntPtr), typeof(IntPtr) } : new[] { typeof(IntPtr) })
            .Concat(monoMethod.GetParameters().Select(it => it.ParameterType.NativeType())).Concat(new[] { typeof(Il2CppMethodInfo*) }).ToArray();

        var managedParameters = new[] { monoMethod.DeclaringType }
            .Concat(monoMethod.GetParameters().Select(it => it.ParameterType)).ToArray();

        var method = new DynamicMethod(
            "Trampoline_" + ExtractSignature(monoMethod) + monoMethod.DeclaringType + monoMethod.Name,
            MethodAttributes.Static | MethodAttributes.Public, CallingConventions.Standard,
            returnsBuffer ? typeof(IntPtr) : monoMethod.ReturnType.NativeType(), nativeParameterTypes,
            monoMethod.DeclaringType, true);

        var signature = new DelegateSupport.MethodSignature(monoMethod, true);
        var delegateType = DelegateSupport.GetOrCreateDelegateType(signature, monoMethod);

        var body = method.GetILGenerator();

        body.BeginExceptionBlock();

        // An injected override that calls back into itself blows the stack, which kills the process with no log at all
        body.Emit(OpCodes.Ldstr, $"{monoMethod.DeclaringType}.{monoMethod.Name}");
        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(GuardStack))!);

        body.Emit(OpCodes.Ldarg, argumentOffset);
        body.Emit(OpCodes.Call,
            typeof(ClassInjectorBase).GetMethod(nameof(ClassInjectorBase.GetMonoObjectFromIl2CppPointer))!);
        body.Emit(OpCodes.Castclass, monoMethod.DeclaringType);

        var indirectVariables = new LocalBuilder[managedParameters.Length];

        for (var i = 1; i < managedParameters.Length; i++)
        {
            var parameter = managedParameters[i];
            if (TrampolineHelpers.IsWrappedStruct(parameter))
            {
                body.Emit(OpCodes.Ldc_I8, Il2CppClassPointerStore.GetNativeClassPointer(parameter).ToInt64());
                body.Emit(OpCodes.Conv_I);
                body.Emit(TrampolineHelpers.IsPassedByValue(parameter) ? OpCodes.Ldarga_S : OpCodes.Ldarg, i + argumentOffset);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(IL2CPP.IsIl2CppNullable(parameter) ? nameof(IL2CPP.BoxNullable) : nameof(IL2CPP.il2cpp_value_box)));
            }
            else
            {
                body.Emit(OpCodes.Ldarg, i + argumentOffset);
            }

            if (parameter.IsValueType) continue;

            void HandleTypeConversion(Type type)
            {
                if (type == typeof(string))
                {
                    body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppStringToManaged))!);
                }
                else if (type.IsInterface)
                {
                    body.Emit(OpCodes.Call, typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!.MakeGenericMethod(type));
                }
                else if (TrampolineHelpers.IsWrappedStruct(type))
                {
                    // Struct values arrive in a box made above for this call alone
                    body.Emit(OpCodes.Call, typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.WrapValueBox))!.MakeGenericMethod(type));
                }
                else if (type.IsSubclassOf(typeof(Il2CppObjectBase)) && !type.IsSubclassOf(typeof(Il2CppArrayBase)))
                {
                    // The pool hands back the managed object of an injected class, a fresh wrapper would lose the subclass
                    body.Emit(OpCodes.Call, typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!.MakeGenericMethod(type));
                }
                else if (type.IsSubclassOf(typeof(Il2CppObjectBase)))
                {
                    var labelNull = body.DefineLabel();
                    var labelNotNull = body.DefineLabel();
                    body.Emit(OpCodes.Dup);
                    body.Emit(OpCodes.Brfalse, labelNull);
                    // We need to directly resolve from all constructors because on mono GetConstructor can cause the following issue:
                    // `Missing field layout info for ...`
                    // This is caused by GetConstructor calling RuntimeTypeHandle.CanCastTo which can fail since right now unhollower emits ALL fields which appear to now work properly
                    body.Emit(OpCodes.Newobj, type.GetConstructors().FirstOrDefault(ci =>
                    {
                        var ps = ci.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType == typeof(IntPtr);
                    })!);
                    body.Emit(OpCodes.Br, labelNotNull);
                    body.MarkLabel(labelNull);
                    body.Emit(OpCodes.Pop);
                    body.Emit(OpCodes.Ldnull);
                    body.MarkLabel(labelNotNull);
                }
            }

            if (parameter.IsByRef)
            {
                var elemType = parameter.GetElementType();

                indirectVariables[i] = body.DeclareLocal(elemType);

                // A pointer sized load only fits a byref to an object or string. A struct behind the byref needs all
                // of its bytes, and bool is one byte on the il2cpp side.
                if (elemType == typeof(bool))
                {
                    body.Emit(OpCodes.Ldind_U1);
                }
                else if (elemType.IsValueType)
                {
                    body.Emit(OpCodes.Ldobj, elemType);
                }
                else if (TrampolineHelpers.IsBoxedStructByRef(parameter))
                {
                    var data = body.DeclareLocal(typeof(IntPtr));
                    body.Emit(OpCodes.Stloc, data);
                    body.Emit(OpCodes.Ldc_I8, Il2CppClassPointerStore.GetNativeClassPointer(elemType).ToInt64());
                    body.Emit(OpCodes.Conv_I);
                    body.Emit(OpCodes.Ldloc, data);
                    body.Emit(OpCodes.Call, TrampolineHelpers.BoxStructAtMethod);
                }
                else
                {
                    body.Emit(OpCodes.Ldind_I);
                }
                HandleTypeConversion(elemType);
                body.Emit(OpCodes.Stloc, indirectVariables[i]);
                body.Emit(OpCodes.Ldloca, indirectVariables[i]);
            }
            else
            {
                HandleTypeConversion(parameter);
            }
        }

        body.Emit(OpCodes.Call, monoMethod);
        LocalBuilder managedReturnVariable = null;
        if (monoMethod.ReturnType != typeof(void))
        {
            managedReturnVariable = body.DeclareLocal(monoMethod.ReturnType);
            body.Emit(OpCodes.Stloc, managedReturnVariable);
        }

        for (var i = 1; i < managedParameters.Length; i++)
        {
            var variable = indirectVariables[i];
            if (variable == null)
                continue;
            if (TrampolineHelpers.IsBoxedStructByRef(managedParameters[i]))
            {
                body.Emit(OpCodes.Ldloc, variable);
                body.Emit(OpCodes.Ldarg_S, i + argumentOffset);
                TrampolineHelpers.EmitCopyBoxedStructTo(body, managedParameters[i]);
                continue;
            }
            body.Emit(OpCodes.Ldarg_S, i + argumentOffset);
            body.Emit(OpCodes.Ldloc, variable);
            var directType = managedParameters[i].GetElementType();
            if (directType == typeof(string))
            {
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp))!);
            }
            else if (!directType.IsValueType)
            {
                if (directType.IsInterface)
                    body.Emit(OpCodes.Castclass, typeof(Il2CppObjectBase));
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtr))!);
            }
            if (!directType.IsValueType)
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.WriteByRef))!);
            else if (InjectorHelpers.StIndOpcodes.TryGetValue(directType, out var stindOpCodde))
                body.Emit(stindOpCodde);
            else
                body.Emit(OpCodes.Stobj, directType);
        }
        // body.Emit(OpCodes.Ret); // breaks coreclr

        var exceptionLocal = body.DeclareLocal(typeof(Exception));
        body.BeginCatchBlock(typeof(Exception));
        body.Emit(OpCodes.Stloc, exceptionLocal);
        body.Emit(OpCodes.Ldloc, exceptionLocal);
        body.Emit(OpCodes.Ldstr, $"{monoMethod.DeclaringType}.{monoMethod.Name}");
        body.Emit(OpCodes.Call, typeof(ClassInjector).GetMethod(nameof(ReportTrampolineException), BindingFlags.Static | BindingFlags.NonPublic)!);

        body.EndExceptionBlock();

        if (managedReturnVariable != null)
        {
            if (returnsBuffer)
            {
                // The buffer pointer is also the return value
                body.Emit(OpCodes.Ldarg_0);
                body.Emit(OpCodes.Ldloc, managedReturnVariable);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtrNotNull))!);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_object_unbox))!);
                body.Emit(OpCodes.Ldc_I4, TrampolineHelpers.ValueSize(monoMethod.ReturnType));
                body.Emit(OpCodes.Cpblk);
                body.Emit(OpCodes.Ldarg_0);
            }
            else if (TrampolineHelpers.IsPassedByValue(monoMethod.ReturnType))
            {
                body.Emit(OpCodes.Ldloc, managedReturnVariable);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtrNotNull))!);
                body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_object_unbox))!);
                body.Emit(OpCodes.Ldobj, monoMethod.ReturnType.NativeType());
            }
            else
            {
                body.Emit(OpCodes.Ldloc, managedReturnVariable);
                if (monoMethod.ReturnType == typeof(string))
                {
                    body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.ManagedStringToIl2Cpp))!);
                }
                else if (!monoMethod.ReturnType.IsValueType)
                {
                    if (monoMethod.ReturnType.IsInterface)
                        body.Emit(OpCodes.Castclass, typeof(Il2CppObjectBase));
                    body.Emit(OpCodes.Call, typeof(IL2CPP).GetMethod(nameof(IL2CPP.Il2CppObjectBaseToPtr))!);
                }
            }
        }

        body.Emit(OpCodes.Ret);

        var @delegate = method.CreateDelegate(delegateType);
        GCHandle.Alloc(@delegate); // pin it forever
        return @delegate;
    }

    private static void ReportTrampolineException(Exception exception, string method)
    {
        var constructing = ConstructingType;
        if (constructing == null)
        {
            Logger.Instance.LogError("Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp: {Message}", exception.ToString());
            return;
        }

        Logger.Instance.LogError(
            "Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp: {Message}{NewLine}{Method} ran from the il2cpp constructor of {Type}, where everything the managed constructor assigns after InvokeBaseConstructor is still null",
            exception.ToString(), Environment.NewLine, method, constructing);
    }

    private static string ExtractSignature(MethodInfo monoMethod)
    {
        var builder = new StringBuilder();
        builder.Append(monoMethod.ReturnType.NativeType().Name);
        // A buffered struct return shares the IntPtr native type with references but not the invoker shape
        builder.Append(TrampolineHelpers.NeedsReturnBuffer(monoMethod.ReturnType) ? "ReturnBuffer" : "");
        // The invoker boxes a value type return with the return type's own class and sizes a buffered return
        // from that class. Neither reaches the native signature, which collapses bool onto byte and keeps only
        // a simple name, so no value type return can share an invoker.
        if (monoMethod.ReturnType != typeof(void) &&
            (monoMethod.ReturnType.IsValueType || TrampolineHelpers.IsWrappedStruct(monoMethod.ReturnType)))
            builder.Append(monoMethod.ReturnType.AssemblyQualifiedName);
        builder.Append(monoMethod.IsStatic ? "" : "This");
        foreach (var parameterInfo in monoMethod.GetParameters())
            builder.Append(parameterInfo.ParameterType.NativeType().Name);
        return builder.ToString();
    }

    private static Type RewriteType(Type type)
    {
        if (type.IsByRef)
            return RewriteType(type.GetElementType()).MakeByRefType();

        if (type.IsValueType && !type.IsEnum)
            return type;

        if (type == typeof(string))
            return type;

        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            if (elementType!.FullName == "System.String") return typeof(Il2CppStringArray);

            var convertedElementType = RewriteType(elementType);
            if (elementType.IsGenericParameter) return typeof(Il2CppArrayBase<>).MakeGenericType(convertedElementType);

            return (convertedElementType.IsValueType ? typeof(Il2CppStructArray<>) : typeof(Il2CppReferenceArray<>))
                .MakeGenericType(convertedElementType);
        }

        if (type.FullName!.StartsWith("System"))
        {
            var fullName = $"Il2Cpp{type.FullName}";
            var resolvedType = Type.GetType($"{fullName}, Il2Cpp{type.Assembly.GetName().Name}", false);
            if (resolvedType != null)
                return resolvedType;

            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .First(t => t != null);
        }

        return type;
    }

    private static string GetIl2CppTypeFullName(Il2CppTypeStruct* typePointer)
    {
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)IL2CPP.il2cpp_class_from_type((IntPtr)typePointer));
        var assembly = UnityVersionHandler.Wrap(UnityVersionHandler.Wrap(klass.Image).Assembly);
        var fullName = new StringBuilder();
        var names = new Stack<string>();
        var declaringType = klass;
        var outerType = klass;
        do
        {
            names.Push(Marshal.PtrToStringUTF8(declaringType.Name) ?? "");
            outerType = declaringType;
        }
        while ((declaringType = UnityVersionHandler.Wrap(declaringType.DeclaringType)) != default);
        var namespaceName = outerType.Namespace != IntPtr.Zero ? Marshal.PtrToStringUTF8(outerType.Namespace) ?? "" : "";

        fullName.Append(namespaceName);
        if (namespaceName.Length > 0)
            fullName.Append('.');
        fullName.Append(string.Join("+", names));

        var assemblyName = Marshal.PtrToStringUTF8(assembly.Name.Name);
        if (assemblyName != "mscorlib")
        {
            fullName.Append(", ");
            fullName.Append(assemblyName);
        }

        return fullName.ToString();
    }

    internal static Type SystemTypeFromIl2CppType(Il2CppTypeStruct* typePointer)
    {
        var fullName = GetIl2CppTypeFullName(typePointer);
        var type = Type.GetType(fullName)
            ?? Type.GetType(fullName.Contains('.') ? "Il2Cpp" + fullName : "Il2Cpp." + fullName)
            ?? throw new NullReferenceException($"Couldn't find System.Type for Il2Cpp type: {fullName}");

        INativeTypeStruct wrappedType = UnityVersionHandler.Wrap(typePointer);
        if (wrappedType.Type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
        {
            Il2CppGenericClass* genericClass = (Il2CppGenericClass*)wrappedType.Data;
            uint argc = genericClass->context.class_inst->type_argc;
            Il2CppTypeStruct** argv = genericClass->context.class_inst->type_argv;
            Type[] genericArguments = new Type[argc];

            for (int i = 0; i < argc; i++)
            {
                genericArguments[i] = SystemTypeFromIl2CppType(argv[i]);
            }
            type = type.MakeGenericType(genericArguments);
        }
        if (wrappedType.ByRef)
            type = type.MakeByRefType();
        return RewriteType(type);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InvokerDelegateMetadataV29(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args, IntPtr* returnValue);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr InvokerDelegate(IntPtr methodPointer, Il2CppMethodInfo* methodInfo, IntPtr obj, IntPtr* args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidCtorDelegate(IntPtr objectPointer);
}
