﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNext.StaticAnalysis
{
	public abstract class NestedInvocationWalker : CSharpSyntaxWalker
	{
		#region Fields & service methods

		private const int MaxDepth = 100; // для борьбы с circular dependencies
		private bool RecursiveAnalysisEnabled() => _nodesStack.Count <= MaxDepth;
		private readonly Compilation _compilation;
		private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = new Dictionary<SyntaxTree, SemanticModel>();
		private readonly ISet<(SyntaxNode, DiagnosticDescriptor)> _reportedDiagnostics = new HashSet<(SyntaxNode, DiagnosticDescriptor)>();
		private readonly Stack<SyntaxNode> _nodesStack = new Stack<SyntaxNode>();
		private readonly HashSet<IMethodSymbol> _methodsInStack = new HashSet<IMethodSymbol>();
		private bool IsMethodInStack(IMethodSymbol symbol) => _methodsInStack.Contains(symbol);

        protected CancellationToken CancellationToken { get; }
		protected void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();
		
		#endregion

        // Синтаксическая нода в оригинальном дереве, с которого начался анализ.
        // Обычно это нода, на которой и нужно показывать диагностику.
        protected SyntaxNode OriginalNode { get; private set; }

		protected NestedInvocationWalker(Compilation compilation, CancellationToken cancellationToken)
		{
			_compilation = compilation;
            CancellationToken = cancellationToken;
		}

		#region Visit

		// Вызов метода
		public override void VisitInvocationExpression(InvocationExpressionSyntax node)
		{
			ThrowIfCancellationRequested();

			if (RecursiveAnalysisEnabled() && node.Parent?.Kind() != SyntaxKind.ConditionalAccessExpression)
			{
				var methodSymbol = GetSymbol<IMethodSymbol>(node);
				VisitMethodSymbol(methodSymbol, node);
			}

			base.VisitInvocationExpression(node);
		}

		// Обращение к property (getter)
		public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
		{
			ThrowIfCancellationRequested();

			if (RecursiveAnalysisEnabled() && node.Parent != null
				&& !node.Parent.IsKind(SyntaxKind.ObjectInitializerExpression)
			    && !(node.Parent is AssignmentExpressionSyntax))
			{
				var propertySymbol = GetSymbol<IPropertySymbol>(node);
				VisitMethodSymbol(propertySymbol?.GetMethod, node);
			}

			base.VisitMemberAccessExpression(node);
		}

		// Присвоение property (setter)
		public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
		{
			ThrowIfCancellationRequested();

			if (RecursiveAnalysisEnabled())
			{
				var propertySymbol = GetSymbol<IPropertySymbol>(node.Left);
				VisitMethodSymbol(propertySymbol?.SetMethod, node);
			}

			base.VisitAssignmentExpression(node);
		}

		// Конструктор
		public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
		{
			ThrowIfCancellationRequested();

			if (RecursiveAnalysisEnabled())
			{
				var methodSymbol = GetSymbol<IMethodSymbol>(node);
				VisitMethodSymbol(methodSymbol, node);
			}

			base.VisitObjectCreationExpression(node);
		}

		// Обращение к property или вызов метода через ?.
		public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
		{
			ThrowIfCancellationRequested();

			if (RecursiveAnalysisEnabled() && node.WhenNotNull != null)
			{
				var propertySymbol = GetSymbol<IPropertySymbol>(node.WhenNotNull);
				var methodSymbol = propertySymbol != null 
					? propertySymbol.GetMethod 
					: GetSymbol<IMethodSymbol>(node.WhenNotNull);

				VisitMethodSymbol(methodSymbol, node);
			}

			base.VisitConditionalAccessExpression(node);
		}

		// Не заходим в лямбды
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
		public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }

        #endregion

		private void VisitMethodSymbol(IMethodSymbol symbol, SyntaxNode originalNode)
		{
			if (symbol?.GetSyntax(CancellationToken) is CSharpSyntaxNode methodNode &&
			    !IsMethodInStack(symbol))
			{
				Push(originalNode, symbol);
				methodNode.Accept(this);
				Pop(symbol);
			}
		}

		// Возвращает семантическую информацию (symbol) для expression
		// (например, вызова метода)
		protected virtual T GetSymbol<T>(ExpressionSyntax node)
			where T : class, ISymbol
		{
			var semanticModel = GetSemanticModel(node.SyntaxTree);

			if (semanticModel != null)
			{
				var symbolInfo = semanticModel.GetSymbolInfo(node, CancellationToken);

				if (symbolInfo.Symbol is T symbol)
					return symbol;

				if (!symbolInfo.CandidateSymbols.IsEmpty)
					return symbolInfo.CandidateSymbols.OfType<T>().FirstOrDefault();
			}

			return null;
		}

		// Получаем / кэшируем семантическую модель для документа
		protected virtual SemanticModel GetSemanticModel(SyntaxTree syntaxTree)
		{
			if (!_compilation.ContainsSyntaxTree(syntaxTree))
				return null;

			if (_semanticModels.TryGetValue(syntaxTree, out var semanticModel))
				return semanticModel;

			semanticModel = _compilation.GetSemanticModel(syntaxTree);
			_semanticModels[syntaxTree] = semanticModel;
			return semanticModel;
		}

		protected virtual void ReportDiagnostic(Action<Diagnostic> reportDiagnostic, DiagnosticDescriptor diagnosticDescriptor,
												SyntaxNode node, params object[] messageArgs)
		{
			var nodeToReport = OriginalNode ?? node;
			var diagnosticKey = (nodeToReport, diagnosticDescriptor);

			if (!_reportedDiagnostics.Contains(diagnosticKey))
			{
				var diagnostic = Diagnostic.Create(diagnosticDescriptor, nodeToReport.GetLocation(), messageArgs);
				reportDiagnostic(diagnostic);
				_reportedDiagnostics.Add(diagnosticKey);
			}
		}

		private void Push(SyntaxNode node, IMethodSymbol symbol)
		{
			if (_nodesStack.Count == 0)
				OriginalNode = node;

			_nodesStack.Push(node);
            _methodsInStack.Add(symbol);
		}

		private void Pop(IMethodSymbol symbol)
		{
			_nodesStack.Pop();
            _methodsInStack.Remove(symbol);

			if (_nodesStack.Count == 0)
				OriginalNode = null;
		}
    }
}
