﻿// Copyright (c) 2018 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	/// <summary>
	/// Transform that converts code patterns like "v != null ? v.M() : null" to "v?.M()"
	/// </summary>
	struct NullPropagationTransform
	{
		internal static bool IsProtectedIfInst(IfInstruction ifInst)
		{
			// We exclude logic.and to avoid turning
			// "logic.and(comp(interfaces != ldnull), call get_Count(interfaces))"
			// into "if ((interfaces?.Count ?? 0) != 0)".
			return (ifInst.MatchLogicAnd(out _, out _) || ifInst.MatchLogicOr(out _, out _))
				&& IfInstruction.IsInConditionSlot(ifInst);
		}

		readonly ILTransformContext context;
		
		public NullPropagationTransform(ILTransformContext context)
		{
			this.context = context;
		}

		enum Mode
		{
			/// <summary>
			/// reference type or generic type (comparison is 'comp(ldloc(testedVar) == null)')
			/// </summary>
			ReferenceType,
			/// <summary>
			/// nullable type, used by value (comparison is 'call get_HasValue(ldloca(testedVar))')
			/// </summary>
			NullableByValue,
			/// <summary>
			/// nullable type, used by reference (comparison is 'call get_HasValue(ldloc(testedVar))')
			/// </summary>
			NullableByReference,
		}

		/// <summary>
		/// Check if "condition ? trueInst : falseInst" can be simplified using the null-conditional operator.
		/// Returns the replacement instruction, or null if no replacement is possible.
		/// </summary>
		internal ILInstruction Run(ILInstruction condition, ILInstruction trueInst, ILInstruction falseInst, Interval ilRange)
		{
			Debug.Assert(context.Settings.NullPropagation);
			Debug.Assert(!condition.MatchLogicNot(out _), "Caller should pass in positive condition");
			if (condition is Comp comp && comp.Left.MatchLdLoc(out var testedVar) && comp.Right.MatchLdNull()) {
				if (comp.LiftingKind != ComparisonLiftingKind.None)
					return null;
				if (comp.Kind == ComparisonKind.Equality) {
					// testedVar == null ? trueInst : falseInst
					return TryNullPropagation(testedVar, falseInst, trueInst, Mode.ReferenceType, ilRange);
				} else if (comp.Kind == ComparisonKind.Inequality) {
					return TryNullPropagation(testedVar, trueInst, falseInst, Mode.ReferenceType, ilRange);
				}
			} else if (NullableLiftingTransform.MatchHasValueCall(condition, out ILInstruction loadInst)) {
				// loadInst.HasValue ? trueInst : falseInst
				if (loadInst.MatchLdLoca(out testedVar)) {
					return TryNullPropagation(testedVar, trueInst, falseInst, Mode.NullableByValue, ilRange);
				} else if (loadInst.MatchLdLoc(out testedVar)) {
					return TryNullPropagation(testedVar, trueInst, falseInst, Mode.NullableByReference, ilRange);
				}
			}
			return null;
		}

		/// <summary>
		/// testedVar != null ? nonNullInst : nullInst
		/// </summary>
		ILInstruction TryNullPropagation(ILVariable testedVar, ILInstruction nonNullInst, ILInstruction nullInst,
			Mode mode, Interval ilRange)
		{
			bool removedRewrapOrNullableCtor = false;
			if (NullableLiftingTransform.MatchNullableCtor(nonNullInst, out _, out var arg)) {
				nonNullInst = arg;
				removedRewrapOrNullableCtor = true;
			} else if (nonNullInst.MatchNullableRewrap(out arg)) {
				nonNullInst = arg;
				removedRewrapOrNullableCtor = true;
			}
			if (!IsValidAccessChain(testedVar, mode, nonNullInst, out var varLoad))
				return null;
			// note: InferType will be accurate in this case because the access chain consists of calls and field accesses
			IType returnType = nonNullInst.InferType();
			if (nullInst.MatchLdNull()) {
				context.Step($"Null propagation (mode={mode}, output=reference type)", nonNullInst);
				// testedVar != null ? testedVar.AccessChain : null
				// => testedVar?.AccessChain
				IntroduceUnwrap(testedVar, varLoad, mode);
				return new NullableRewrap(nonNullInst) { ILRange = ilRange };
			} else if (nullInst.MatchDefaultValue(out var type) && type.IsKnownType(KnownTypeCode.NullableOfT)) {
				context.Step($"Null propagation (mode={mode}, output=value type)", nonNullInst);
				// testedVar != null ? testedVar.AccessChain : default(T?)
				// => testedVar?.AccessChain
				IntroduceUnwrap(testedVar, varLoad, mode);
				return new NullableRewrap(nonNullInst) { ILRange = ilRange };
			} else if (!removedRewrapOrNullableCtor && NullableType.IsNonNullableValueType(returnType)) {
				context.Step($"Null propagation (mode={mode}, output=null coalescing)", nonNullInst);
				// testedVar != null ? testedVar.AccessChain : nullInst
				// => testedVar?.AccessChain ?? nullInst
				// (only valid if AccessChain returns a non-nullable value)
				IntroduceUnwrap(testedVar, varLoad, mode);
				return new NullCoalescingInstruction(
					NullCoalescingKind.NullableWithValueFallback,
					new NullableRewrap(nonNullInst),
					nullInst
				) {
					UnderlyingResultType = nullInst.ResultType,
					ILRange = ilRange
				};
			}
			return null;
		}

		/// <summary>
		/// if (x != null) x.AccessChain();
		/// => x?.AccessChain();
		/// </summary>
		internal void RunStatements(Block block, int pos)
		{
			var ifInst = block.Instructions[pos] as IfInstruction;
			if (ifInst == null || !ifInst.FalseInst.MatchNop())
				return;
			if (ifInst.Condition is Comp comp && comp.Kind == ComparisonKind.Inequality
				&& comp.Left.MatchLdLoc(out var testedVar) && comp.Right.MatchLdNull()) {
				TryNullPropForVoidCall(testedVar, Mode.ReferenceType, ifInst.TrueInst as Block, ifInst);
			} else if (NullableLiftingTransform.MatchHasValueCall(ifInst.Condition, out ILInstruction arg)) {
				if (arg.MatchLdLoca(out testedVar)) {
					TryNullPropForVoidCall(testedVar, Mode.NullableByValue, ifInst.TrueInst as Block, ifInst);
				} else if (arg.MatchLdLoc(out testedVar)) {
					TryNullPropForVoidCall(testedVar, Mode.NullableByReference, ifInst.TrueInst as Block, ifInst);
				}
			}
		}

		void TryNullPropForVoidCall(ILVariable testedVar, Mode mode, Block body, IfInstruction ifInst)
		{
			if (body == null || body.Instructions.Count != 1)
				return;
			var bodyInst = body.Instructions[0];
			if (bodyInst.MatchNullableRewrap(out var arg)) {
				bodyInst = arg;
			}
			if (!IsValidAccessChain(testedVar, mode, bodyInst, out var varLoad))
				return;
			context.Step($"Null-propagation (mode={mode}, output=void call)", body);
			// if (testedVar != null) { testedVar.AccessChain(); }
			// => testedVar?.AccessChain();
			IntroduceUnwrap(testedVar, varLoad, mode);
			ifInst.ReplaceWith(new NullableRewrap(
				bodyInst
			) { ILRange = ifInst.ILRange });
		}

		bool IsValidAccessChain(ILVariable testedVar, Mode mode, ILInstruction inst, out ILInstruction finalLoad)
		{
			finalLoad = null;
			int chainLength = 0;
			while (true) {
				if (IsValidEndOfChain()) {
					// valid end of chain
					finalLoad = inst;
					return chainLength >= 1;
				} else if (inst.MatchLdFld(out var target, out _)) {
					inst = target;
				} else if (inst is CallInstruction call && call.OpCode != OpCode.NewObj) {
					if (call.Arguments.Count == 0) {
						return false;
					}
					if (call.Method.IsStatic && (!call.Method.IsExtensionMethod || !CanTransformToExtensionMethodCall(call))) {
						return false; // only instance or extension methods can be called with ?. syntax
					}
					if (call.Method.IsAccessor && !IsGetter(call.Method)) {
						return false; // setter/adder/remover cannot be called with ?. syntax
					}
					inst = call.Arguments[0];
					if ((call.ConstrainedTo ?? call.Method.DeclaringType).IsReferenceType == false && inst.MatchAddressOf(out var arg)) {
						inst = arg;
					}
					// ensure the access chain does not contain any 'nullable.unwrap' that aren't directly part of the chain
					for (int i = 1; i < call.Arguments.Count; ++i) {
						if (call.Arguments[i].HasFlag(InstructionFlags.MayUnwrapNull)) {
							return false;
						}
					}
				} else if (inst is NullableUnwrap unwrap) {
					inst = unwrap.Argument;
				} else {
					// unknown node -> invalid chain
					return false;
				}
				chainLength++;
			}

			bool IsValidEndOfChain()
			{
				switch (mode) {
					case Mode.ReferenceType:
						// either reference type (expect: ldloc(testedVar)) or unconstrained generic type (expect: ldloca(testedVar)).
						return inst.MatchLdLocRef(testedVar);
					case Mode.NullableByValue:
						return NullableLiftingTransform.MatchGetValueOrDefault(inst, testedVar);
					case Mode.NullableByReference:
						return NullableLiftingTransform.MatchGetValueOrDefault(inst, out ILInstruction arg)
							&& arg.MatchLdLoc(testedVar);
					default:
						throw new ArgumentOutOfRangeException("mode");
				}
			}
		}

		bool CanTransformToExtensionMethodCall(CallInstruction call)
		{
			if (call.Method.Parameters.Count == 0) return false;
			var targetType = call.Method.Parameters.Select(p => new ResolveResult(p.Type)).First();
			var paramTypes = call.Method.Parameters.Skip(1).Select(p => new ResolveResult(p.Type)).ToArray();
			var paramNames = call.Method.Parameters.SelectArray(p => p.Name);
			var typeArgs = call.Method.TypeArguments.ToArray();
			var resolveContext = new CSharp.TypeSystem.CSharpTypeResolveContext(context.TypeSystem.Compilation.MainAssembly, context.UsingScope);
			var resolver = new CSharp.Resolver.CSharpResolver(resolveContext);
			return CSharp.Transforms.IntroduceExtensionMethods.CanTransformToExtensionMethodCall(resolver, call.Method, typeArgs, targetType, paramTypes);
		}

		static bool IsGetter(IMethod method)
		{
			return method.AccessorOwner is IProperty p && p.Getter == method;
		}

		private void IntroduceUnwrap(ILVariable testedVar, ILInstruction varLoad, Mode mode)
		{
			var oldParentChildren = varLoad.Parent.Children;
			var oldChildIndex = varLoad.ChildIndex;
			ILInstruction replacement;
			switch (mode) {
				case Mode.ReferenceType:
					// Wrap varLoad in nullable.unwrap:
					replacement = new NullableUnwrap(varLoad.ResultType, varLoad, refInput: varLoad.ResultType == StackType.Ref);
					break;
				case Mode.NullableByValue:
					Debug.Assert(NullableLiftingTransform.MatchGetValueOrDefault(varLoad, testedVar));
					replacement = new NullableUnwrap(
						varLoad.ResultType,
						new LdLoc(testedVar) { ILRange = varLoad.Children[0].ILRange }
					) { ILRange = varLoad.ILRange };
					break;
				case Mode.NullableByReference:
					replacement = new NullableUnwrap(
						varLoad.ResultType,
						new LdLoc(testedVar) { ILRange = varLoad.Children[0].ILRange },
						refInput: true
					) { ILRange = varLoad.ILRange };
					break;
				default:
					throw new ArgumentOutOfRangeException("mode");
			}
			oldParentChildren[oldChildIndex] = replacement;
		}
	}

	class NullPropagationStatementTransform : IStatementTransform
	{
		public void Run(Block block, int pos, StatementTransformContext context)
		{
			if (!context.Settings.NullPropagation)
				return;
			new NullPropagationTransform(context).RunStatements(block, pos);
		}
	}
}
