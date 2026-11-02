using System;
using System.Reflection;
using System.Reflection.Emit;
using Cairo;
using Xunit;

public sealed class CairoSurfaceConstructorAbiTests
{
	[Fact]
	public void ProtectedFourArgumentConstructorRemainsCallableWithoutNativeCairo ()
	{
		ConstructorInfo constructor = typeof (Surface).GetConstructor (
			BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
			new[] { typeof (IntPtr), typeof (bool), typeof (bool), typeof (bool) }, modifiers: null);
		Assert.NotNull (constructor);
		int denied = CairoNativeGuard.DeniedCount;

		var assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("CairoSurfaceAbiProbe"), AssemblyBuilderAccess.Run);
		ModuleBuilder module = assembly.DefineDynamicModule ("main");
		TypeBuilder type = module.DefineType ("ManagedSurfaceSubclass", TypeAttributes.Public, typeof (Surface));
		ConstructorBuilder derivedConstructor = type.DefineConstructor (MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
		ILGenerator il = derivedConstructor.GetILGenerator ();
		il.Emit (OpCodes.Ldarg_0);
		il.Emit (OpCodes.Ldc_I4_0);
		il.Emit (OpCodes.Conv_I);
		il.Emit (OpCodes.Ldc_I4_0);
		il.Emit (OpCodes.Ldc_I4_0);
		il.Emit (OpCodes.Ldc_I4_1);
		il.Emit (OpCodes.Call, constructor);
		il.Emit (OpCodes.Ret);
		Type derived = type.CreateType ();
		using var instance = (Surface)Activator.CreateInstance (derived);

		Assert.Equal (denied, CairoNativeGuard.DeniedCount);
		if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
	}
}
