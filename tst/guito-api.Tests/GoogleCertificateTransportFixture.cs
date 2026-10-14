using System.Reflection;
using System.Reflection.Emit;
using Google.Apis.Auth;

namespace GuitoApi.Tests;

/// <summary>Connects the pinned Google SDK's per-settings certificate test hook to a real fake HTTP transport.</summary>
internal static class GoogleCertificateTransportFixture
{
    private static readonly Type CacheType = BuildCacheType();

    public static GoogleJsonWebSignature.ValidationSettings Settings(HttpClient transport)
    {
        var settings = new GoogleJsonWebSignature.ValidationSettings();
        var cache = Activator.CreateInstance(CacheType, transport);
        typeof(GoogleJsonWebSignature.ValidationSettings).GetProperty("CertificateCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, cache);
        return settings;
    }

    private static Type BuildCacheType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Guito.GoogleTransportFixture"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixture");
        AllowSdkInternals(assembly, module);
        var baseType = typeof(GoogleJsonWebSignature).Assembly.GetType("Google.Apis.Auth.SignedTokenVerification+CertificateCacheBase", throwOnError: true)!;
        var type = module.DefineType("TransportCertificateCache", TypeAttributes.Public, baseType);
        var client = type.DefineField("client", typeof(HttpClient), FieldAttributes.Private);
        DefineConstructor(type, baseType, client);
        DefineFetch(type, baseType, client);
        return type.CreateType()!;
    }

    private static void DefineConstructor(TypeBuilder type, Type baseType, FieldBuilder client)
    {
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(HttpClient)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, typeof(Google.Apis.Util.SystemClock).GetField("Default")!);
        il.Emit(OpCodes.Call, baseType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single());
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, client);
        il.Emit(OpCodes.Ret);
    }

    private static void DefineFetch(TypeBuilder type, Type baseType, FieldBuilder client)
    {
        var method = type.DefineMethod("FetchCertificatesAsync", MethodAttributes.Family | MethodAttributes.Virtual,
            typeof(Task<string>), [typeof(string)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, client);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(HttpClient).GetMethod(nameof(HttpClient.GetStringAsync), [typeof(string)])!);
        il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(method, baseType.GetMethod("FetchCertificatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!);
    }

    // SDK keeps its documented CertificateCache testing hook internal. Access is confined to this dynamic test assembly.
    private static void AllowSdkInternals(AssemblyBuilder assembly, ModuleBuilder module)
    {
        var attribute = module.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
            TypeAttributes.Public, typeof(Attribute));
        var constructor = attribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
        il.Emit(OpCodes.Ret);
        var attributeType = attribute.CreateType()!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(attributeType.GetConstructor([typeof(string)])!,
            [typeof(GoogleJsonWebSignature).Assembly.GetName().Name!]));
    }
}
