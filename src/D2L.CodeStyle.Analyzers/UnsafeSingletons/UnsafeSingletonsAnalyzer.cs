﻿using System.Collections.Immutable;
using D2L.CodeStyle.Analyzers.Common;
using D2L.CodeStyle.Analyzers.Common.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace D2L.CodeStyle.Analyzers.UnsafeSingletons {
	[DiagnosticAnalyzer( LanguageNames.CSharp )]
	public sealed class UnsafeSingletonsAnalyzer : DiagnosticAnalyzer {
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create( 
			Diagnostics.UnsafeSingletonField,
			Diagnostics.SingletonRegistrationTypeUnknown,
			Diagnostics.RegistrationKindUnknown
		);

		private readonly MutabilityInspectionResultFormatter m_resultFormatter = new MutabilityInspectionResultFormatter();

		public override void Initialize( AnalysisContext context ) {
			context.RegisterCompilationStartAction( RegisterAnalysis );
		}

		private void RegisterAnalysis( CompilationStartAnalysisContext context ) {
			var inspector = new MutabilityInspector( new KnownImmutableTypes( context.Compilation.Assembly ) );

			DependencyRegistry dependencyRegistry;
			if( !DependencyRegistry.TryCreateRegistry( context.Compilation, out dependencyRegistry ) ) {
				return;
			}

			context.RegisterSyntaxNodeAction(
				ctx => AnalyzeInvocation( ctx, inspector, dependencyRegistry ),
				SyntaxKind.InvocationExpression
			);
		}

		private void AnalyzeInvocation( SyntaxNodeAnalysisContext context, MutabilityInspector inspector, DependencyRegistry registry ) {
			var root = context.Node as InvocationExpressionSyntax;
			if( root == null ) {
				return;
			}
			var method = context.SemanticModel.GetSymbolInfo( root ).Symbol as IMethodSymbol;
			if( method == null ) {
				return;
			}

			if( !registry.IsRegistationMethod( method ) ) {
				return;
			}

			DependencyRegistrationExpression dependencyRegistrationExpresion;
			if( !registry.TryMapRegistrationMethod( 
				method, 
				root.ArgumentList.Arguments, 
				context.SemanticModel, 
				out dependencyRegistrationExpresion 
			) ) {
				// we expected a mapped registration method, but didn't get one
				// so we fail
				var diagnostic = Diagnostic.Create(
					Diagnostics.RegistrationKindUnknown,
					root.GetLocation()
				);
				context.ReportDiagnostic( diagnostic );
				return;
			}

			var dependencyRegistration = dependencyRegistrationExpresion.GetRegistration( 
				method,
				root.ArgumentList.Arguments,
				context.SemanticModel
			);
			if( dependencyRegistration == null ) {
				// this happens when ObjectScope is a variable
				// or the number of arguments doesn't match the
				// number of parameters
				// sometimes this is a compiler error, other times it isn't
				// but we should fail because we can't analyze it
				var diagnostic = Diagnostic.Create(
					Diagnostics.RegistrationKindUnknown,
					root.GetLocation()
				);
				context.ReportDiagnostic( diagnostic );
				return;
			}

			if( dependencyRegistration.ObjectScope != ObjectScope.Singleton ) {
				// we only care about singletons
				return;
			}

			var typeToInspect = GetTypeToInspect( dependencyRegistration );
			if( typeToInspect.IsNullOrErrorType() ) {
				// we expected a type, but didn't get one, so fail
				var diagnostic = Diagnostic.Create(
					Diagnostics.SingletonRegistrationTypeUnknown,
					root.GetLocation()
				);
				context.ReportDiagnostic( diagnostic );
				return;
			}

			// TODO: it probably makes more sense to iterate over the fields and emit diagnostics tied to those individual fields for more accurate red-squigglies
			// a DI singleton should be capable of having multiple diagnostics come out of it
			var flags = MutabilityInspectionFlags.AllowUnsealed | MutabilityInspectionFlags.IgnoreImmutabilityAttribute;
			var result = inspector.InspectType( typeToInspect, context.Compilation.Assembly, flags );
			if( result.IsMutable ) {
				var reason = m_resultFormatter.Format( result );
				var diagnostic = Diagnostic.Create(
					Diagnostics.UnsafeSingletonField,
					root.GetLocation(),
					typeToInspect.GetFullTypeNameWithGenericArguments(),
					reason
				);
				context.ReportDiagnostic( diagnostic );
			}
		}

		private ITypeSymbol GetTypeToInspect( DependencyRegistration registration ) {
			// if we have a concrete type, use it; otherwise, use the dependency type
			if( !registration.IsFactoryRegistration && !registration.ConcreteType.IsNullOrErrorType() ) {
				return registration.ConcreteType;
			}
			return registration.DependencyType;
		}

	}
}
