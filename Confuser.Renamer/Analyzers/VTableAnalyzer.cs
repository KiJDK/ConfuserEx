﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Confuser.Core;
using Confuser.Renamer.References;
using Confuser.Renamer.Services;
using dnlib.DotNet;

namespace Confuser.Renamer.Analyzers {
	public class VTableAnalyzer : IRenamer {
		void IRenamer.Analyze(IConfuserContext context, INameService service, IProtectionParameters parameters, IDnlibDef def) {
			if (service is NameService nameService) {
				switch (def) {
					case TypeDef typeDef:
						Analyze(context, nameService, typeDef);
						break;
					case MethodDef methodDef:
						Analyze(context, nameService, methodDef);
						break;
				}
			}
		}

		internal static void Analyze(IConfuserContext context, NameService service, TypeDef type) {
			if (type.IsInterface)
				return;

			var vTbl = service.GetVTables()[type];
			foreach (var ifaceVTbl in vTbl.InterfaceSlots.Values) {
				foreach (var slot in ifaceVTbl) {
					if (slot.Overrides == null)
						continue;
					Debug.Assert(slot.Overrides.MethodDef.DeclaringType.IsInterface);
					// A method in base type can implements an interface method for a
					// derived type. If the base type/interface is not in our control, we should
					// not rename the methods.
					bool baseUnderCtrl = context.Modules.Contains(slot.MethodDef.DeclaringType.Module as ModuleDefMD);
					bool ifaceUnderCtrl = context.Modules.Contains(slot.Overrides.MethodDef.DeclaringType.Module as ModuleDefMD);
					if ((!baseUnderCtrl && ifaceUnderCtrl) || !service.CanRename(context, slot.MethodDef)) {
						service.SetCanRename(context, slot.Overrides.MethodDef, false);
					}
					else if (baseUnderCtrl && !ifaceUnderCtrl || !service.CanRename(context, slot.Overrides.MethodDef)) {
						service.SetCanRename(context, slot.MethodDef, false);
					}
				}
			}
		}

		internal static void Analyze(IConfuserContext context, NameService service, MethodDef method) {
			if (!method.IsVirtual)
				return;

			var vTbl = service.GetVTables()[method.DeclaringType];
			VTableSignature sig = VTableSignature.FromMethod(method);
			var slots = vTbl.FindSlots(method);

			if (!method.IsAbstract) {
				foreach (var slot in slots) {
					if (slot.Overrides == null)
						continue;

					SetupOverwriteReferences(context, service, slot, method.Module);
				}
			}
			else {
				foreach (var slot in slots) {
					if (slot.Overrides == null)
						continue;
					service.SetCanRename(context, method, false);
					service.SetCanRename(context, slot.Overrides.MethodDef, false);
				}
			}
		}

		private static void AddImportReference(IConfuserContext context, INameService service, ModuleDef module, MethodDef method, MemberRef methodRef) {
			if (method.Module != module && context.Modules.Contains((ModuleDefMD)module)) {
				var declType = (TypeRef)methodRef.DeclaringType.ScopeType;
				service.AddReference(context, method.DeclaringType, new TypeRefReference(declType, method.DeclaringType));
				service.AddReference(context, method, new MemberRefReference(methodRef, method));

				var typeRefs = methodRef.MethodSig.Params.SelectMany(param => param.FindTypeRefs()).ToList();
				typeRefs.AddRange(methodRef.MethodSig.RetType.FindTypeRefs());
				typeRefs.AddRange(methodRef.DeclaringType.ToTypeSig().FindTypeRefs());
				foreach (var typeRef in typeRefs) {
					SetupTypeReference(context, service, module, typeRef);
				}
			}
		}

		private static void SetupTypeReference(IConfuserContext context, INameService service, ModuleDef module, ITypeDefOrRef typeDefOrRef) {
			if (!(typeDefOrRef is TypeRef typeRef)) return;

			var def = typeRef.ResolveTypeDefThrow();
			if (def.Module != module && context.Modules.Contains((ModuleDefMD)def.Module))
				service.AddReference(context, def, new TypeRefReference(typeRef, def));
		}

		private static GenericInstSig SetupSignatureReferences(IConfuserContext context, INameService service, ModuleDef module, GenericInstSig typeSig) {
			var genericType = SetupSignatureReferences(context, service, module, typeSig.GenericType);
			var genericArguments = typeSig.GenericArguments.Select(a => SetupSignatureReferences(context, service, module, a)).ToList();
			return new GenericInstSig(genericType, genericArguments);
		}

		private static T SetupSignatureReferences<T>(IConfuserContext context, INameService service, ModuleDef module, T typeSig) where T : TypeSig {
			var asTypeRef = typeSig.TryGetTypeRef();
			if (asTypeRef != null) {
				SetupTypeReference(context, service, module, asTypeRef);
			}
			return typeSig;
		}

		private static void SetupOverwriteReferences(IConfuserContext context, INameService service, VTableSlot slot, ModuleDef module) {
			var methodDef = slot.MethodDef;
			var baseSlot = slot.Overrides;
			var baseMethodDef = baseSlot.MethodDef;

			var overrideRef = new OverrideDirectiveReference(slot, baseSlot);
			service.AddReference(context, methodDef, overrideRef);
			service.AddReference(context, slot.Overrides.MethodDef, overrideRef);

			var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);

			IMethodDefOrRef target;
			if (baseSlot.MethodDefDeclType is GenericInstSig declType) {
				var signature = SetupSignatureReferences(context, service, module, declType);
				MemberRef targetRef = new MemberRefUser(module, baseMethodDef.Name, baseMethodDef.MethodSig, signature.ToTypeDefOrRef());
				targetRef = importer.Import(targetRef);
				service.AddReference(context, baseMethodDef, new MemberRefReference(targetRef, baseMethodDef));

				target = targetRef;
			}
			else {
				target = baseMethodDef;
				if (target.Module != module) {
					target = (IMethodDefOrRef)importer.Import(baseMethodDef);
					if (target is MemberRef memberRef)
						service.AddReference(context, baseMethodDef, new MemberRefReference(memberRef, baseMethodDef));
				}
			}

			target.MethodSig = importer.Import(target.MethodSig);
			if (target is MemberRef methodRef)
				AddImportReference(context, service, module, baseMethodDef, methodRef);

			if (methodDef.Overrides.Any(impl => IsMatchingOverride(impl, target)))
				return;

			methodDef.Overrides.Add(new MethodOverride(methodDef, target));
		}

		private static bool IsMatchingOverride(MethodOverride methodOverride, IMethodDefOrRef targetMethod) {
			SigComparer comparer = default;

			var targetDeclTypeDef = targetMethod.DeclaringType.ResolveTypeDef();
			var overrideDeclTypeDef = methodOverride.MethodDeclaration.DeclaringType.ResolveTypeDef();
			if (!comparer.Equals(targetDeclTypeDef, overrideDeclTypeDef))
				return false;

			var targetMethodSig = targetMethod.MethodSig;
			var overrideMethodSig = methodOverride.MethodDeclaration.MethodSig;
			if (methodOverride.MethodDeclaration.DeclaringType is TypeSpec spec && spec.TypeSig is GenericInstSig genericInstSig) {
				overrideMethodSig = GenericArgumentResolver.Resolve(overrideMethodSig, genericInstSig.GenericArguments);
			}

			return comparer.Equals(targetMethodSig, overrideMethodSig);
		}

		public void PreRename(IConfuserContext context, INameService service, IProtectionParameters parameters,
			IDnlibDef def) {
			//
		}

		public void PostRename(IConfuserContext context, INameService service, IProtectionParameters parameters,
			IDnlibDef def) {
			if (!(def is MethodDef method) || !method.IsVirtual || method.Overrides.Count == 0)
				return;

			var methods = new HashSet<IMethodDefOrRef>(MethodDefOrRefComparer.Instance);
			method.Overrides
				  .RemoveWhere(impl => MethodDefOrRefComparer.Instance.Equals(impl.MethodDeclaration, method));
		}

		private sealed class MethodDefOrRefComparer : IEqualityComparer<IMethodDefOrRef> {
			public static readonly MethodDefOrRefComparer Instance = new MethodDefOrRefComparer();

			private MethodDefOrRefComparer() {
			}

			public bool Equals(IMethodDefOrRef x, IMethodDefOrRef y) =>
				new SigComparer().Equals(x, y) && new SigComparer().Equals(x.DeclaringType, y.DeclaringType);

			public int GetHashCode(IMethodDefOrRef obj) =>
				new SigComparer().GetHashCode(obj) * 5 + new SigComparer().GetHashCode(obj.DeclaringType);
		}
	}
}
