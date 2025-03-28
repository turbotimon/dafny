using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using Microsoft.Dafny.Auditor;

namespace Microsoft.Dafny;

public class Method : MemberDecl, TypeParameter.ParentType,
  IMethodCodeContext, ICanFormat, IHasDocstring, IHasSymbolChildren, ICanAutoRevealDependencies, ICanVerify {
  public override IEnumerable<INode> Children => new Node[] { Body, Decreases }.Where(x => x != null).
    Concat(Ins).Concat(Outs).Concat<Node>(TypeArgs).
    Concat(Req).Concat(Ens).Concat(Reads.Expressions).Concat(Mod.Expressions);
  public override IEnumerable<INode> PreResolveChildren => Children;

  public override string WhatKind => "method";
  public bool SignatureIsOmitted { get { return SignatureEllipsis != null; } }
  public readonly IToken SignatureEllipsis;
  public readonly bool IsByMethod;
  public bool MustReverify;
  public bool IsEntryPoint = false;
  public readonly List<TypeParameter> TypeArgs;
  public readonly List<Formal> Ins;
  public readonly List<Formal> Outs;
  public readonly List<AttributedExpression> Req;
  public readonly Specification<FrameExpression> Reads;
  public readonly Specification<FrameExpression> Mod;
  public readonly List<AttributedExpression> Ens;
  public readonly Specification<Expression> Decreases;
  [FilledInDuringResolution] public bool IsRecursive;
  [FilledInDuringResolution] public bool IsTailRecursive;
  [FilledInDuringResolution] public Function FunctionFromWhichThisIsByMethodDecl;
  public readonly ISet<IVariable> AssignedAssumptionVariables = new HashSet<IVariable>();
  public Method OverriddenMethod;
  public Method Original => OverriddenMethod == null ? this : OverriddenMethod.Original;
  public override bool IsOverrideThatAddsBody => base.IsOverrideThatAddsBody && Body != null;

  public bool HasPostcondition =>
    Ens.Count > 0 || Outs.Any(f => f.Type.AsSubsetType is not null);

  public bool HasPrecondition =>
    Req.Count > 0 || Ins.Any(f => f.Type.AsSubsetType is not null);

  public override IEnumerable<Assumption> Assumptions(Declaration decl) {
    foreach (var a in base.Assumptions(this)) {
      yield return a;
    }

    if (Body is null && HasPostcondition && EnclosingClass.EnclosingModuleDefinition.ModuleKind == ModuleKindEnum.Concrete && !HasExternAttribute) {
      yield return new Assumption(this, tok, AssumptionDescription.NoBody(IsGhost));
    }

    if (HasExternAttribute && HasPostcondition && !HasAxiomAttribute) {
      yield return new Assumption(this, tok, AssumptionDescription.ExternWithPostcondition);
    }

    if (HasExternAttribute && HasPrecondition && !HasAxiomAttribute) {
      yield return new Assumption(this, tok, AssumptionDescription.ExternWithPrecondition);
    }

    if (Attributes.Contains(Reads.Attributes, Attributes.AssumeConcurrentAttributeName)) {
      yield return new Assumption(this, tok, AssumptionDescription.HasAssumeConcurrentAttribute(false));
    }
    if (Attributes.Contains(Mod.Attributes, Attributes.AssumeConcurrentAttributeName)) {
      yield return new Assumption(this, tok, AssumptionDescription.HasAssumeConcurrentAttribute(true));
    }

    if (AllowsNontermination) {
      yield return new Assumption(this, tok, AssumptionDescription.MayNotTerminate);
    }

    foreach (var c in this.Descendants()) {
      foreach (var a in (c as Node)?.Assumptions(this) ?? Enumerable.Empty<Assumption>()) {
        yield return a;
      }
    }
  }

  public override IEnumerable<Expression> SubExpressions {
    get {
      foreach (var formal in Ins.Where(f => f.DefaultValue != null)) {
        yield return formal.DefaultValue;
      }
      foreach (var e in Req) {
        yield return e.E;
      }
      foreach (var e in Reads.Expressions) {
        yield return e.E;
      }
      foreach (var e in Mod.Expressions) {
        yield return e.E;
      }
      foreach (var e in Ens) {
        yield return e.E;
      }
      foreach (var e in Decreases.Expressions) {
        yield return e;
      }
    }
  }

  [ContractInvariantMethod]
  void ObjectInvariant() {
    Contract.Invariant(cce.NonNullElements(TypeArgs));
    Contract.Invariant(cce.NonNullElements(Ins));
    Contract.Invariant(cce.NonNullElements(Outs));
    Contract.Invariant(cce.NonNullElements(Req));
    Contract.Invariant(Reads != null);
    Contract.Invariant(Mod != null);
    Contract.Invariant(cce.NonNullElements(Ens));
    Contract.Invariant(Decreases != null);
  }

  public Method(Cloner cloner, Method original) : base(cloner, original) {
    this.TypeArgs = cloner.CloneResolvedFields ? original.TypeArgs : original.TypeArgs.ConvertAll(cloner.CloneTypeParam);
    this.Ins = original.Ins.ConvertAll(p => cloner.CloneFormal(p, false));
    if (original.Outs != null) {
      this.Outs = original.Outs.ConvertAll(p => cloner.CloneFormal(p, false));
    }

    this.Req = original.Req.ConvertAll(cloner.CloneAttributedExpr);
    this.Reads = cloner.CloneSpecFrameExpr(original.Reads);
    this.Mod = cloner.CloneSpecFrameExpr(original.Mod);
    this.Decreases = cloner.CloneSpecExpr(original.Decreases);
    this.Ens = original.Ens.ConvertAll(cloner.CloneAttributedExpr);
    this.Body = cloner.CloneMethodBody(original);
    this.SignatureEllipsis = original.SignatureEllipsis;
    this.IsByMethod = original.IsByMethod;
  }

  public Method(RangeToken rangeToken, Name name,
    bool hasStaticKeyword, bool isGhost,
    [Captured] List<TypeParameter> typeArgs,
    [Captured] List<Formal> ins, [Captured] List<Formal> outs,
    [Captured] List<AttributedExpression> req,
    [Captured] Specification<FrameExpression> reads,
    [Captured] Specification<FrameExpression> mod,
    [Captured] List<AttributedExpression> ens,
    [Captured] Specification<Expression> decreases,
    [Captured] BlockStmt body,
    Attributes attributes, IToken signatureEllipsis, bool isByMethod = false)
    : base(rangeToken, name, hasStaticKeyword, isGhost, attributes, signatureEllipsis != null) {
    Contract.Requires(rangeToken != null);
    Contract.Requires(name != null);
    Contract.Requires(cce.NonNullElements(typeArgs));
    Contract.Requires(cce.NonNullElements(ins));
    Contract.Requires(cce.NonNullElements(outs));
    Contract.Requires(cce.NonNullElements(req));
    Contract.Requires(reads != null);
    Contract.Requires(mod != null);
    Contract.Requires(cce.NonNullElements(ens));
    Contract.Requires(decreases != null);
    this.TypeArgs = typeArgs;
    this.Ins = ins;
    this.Outs = outs;
    this.Reads = reads;
    this.Req = req;
    this.Mod = mod;
    this.Ens = ens;
    this.Decreases = decreases;
    Body = body;
    this.SignatureEllipsis = signatureEllipsis;
    this.IsByMethod = isByMethod;
    MustReverify = false;
  }

  bool ICodeContext.IsGhost { get { return this.IsGhost; } }
  List<TypeParameter> ICodeContext.TypeArgs { get { return this.TypeArgs; } }
  List<Formal> ICodeContext.Ins { get { return this.Ins; } }
  List<Formal> IMethodCodeContext.Outs { get { return this.Outs; } }
  Specification<FrameExpression> IMethodCodeContext.Modifies { get { return Mod; } }
  string ICallable.NameRelativeToModule {
    get {
      if (EnclosingClass is DefaultClassDecl) {
        return Name;
      } else {
        return EnclosingClass.Name + "." + Name;
      }
    }
  }
  Specification<Expression> ICallable.Decreases { get { return this.Decreases; } }
  bool _inferredDecr;
  bool ICallable.InferredDecreases {
    set { _inferredDecr = value; }
    get { return _inferredDecr; }
  }

  public virtual bool AllowsAllocation => true;

  ModuleDefinition IASTVisitorContext.EnclosingModule {
    get {
      Contract.Assert(this.EnclosingClass != null);  // this getter is supposed to be called only after signature-resolution is complete
      return this.EnclosingClass.EnclosingModuleDefinition;
    }
  }
  bool ICodeContext.MustReverify { get { return this.MustReverify; } }
  public bool AllowsNontermination {
    get {
      return Contract.Exists(Decreases.Expressions, e => e is WildcardExpr);
    }
  }

  public override string GetCompileName(DafnyOptions options) {
    var nm = base.GetCompileName(options);
    if (nm == Dafny.Compilers.SinglePassCompiler.DefaultNameMain && IsStatic && !IsEntryPoint) {
      // for a static method that is named "Main" but is not a legal "Main" method,
      // change its name.
      nm = EnclosingClass.Name + "_" + nm;
    }

    return nm;
  }

  public BlockStmt Body { get; set; }

  public bool IsLemmaLike => this is Lemma || this is TwoStateLemma || this is ExtremeLemma || this is PrefixLemma;

  public bool SetIndent(int indentBefore, TokenNewIndentCollector formatter) {
    formatter.SetMethodLikeIndent(StartToken, OwnedTokens, indentBefore);
    if (BodyStartTok.line > 0) {
      formatter.SetDelimiterIndentedRegions(BodyStartTok, indentBefore);
    }

    formatter.SetFormalsIndentation(Ins);
    formatter.SetFormalsIndentation(Outs);
    foreach (var req in Req) {
      formatter.SetAttributedExpressionIndentation(req, indentBefore + formatter.SpaceTab);
    }

    foreach (var read in Reads.Expressions) {
      formatter.SetFrameExpressionIndentation(read, indentBefore + formatter.SpaceTab);
    }

    foreach (var mod in Mod.Expressions) {
      formatter.SetFrameExpressionIndentation(mod, indentBefore + formatter.SpaceTab);
    }

    foreach (var ens in Ens) {
      formatter.SetAttributedExpressionIndentation(ens, indentBefore + formatter.SpaceTab);
    }

    foreach (var dec in Decreases.Expressions) {
      formatter.SetDecreasesExpressionIndentation(dec, indentBefore + formatter.SpaceTab);
      formatter.SetExpressionIndentation(dec);
    }

    if (Body != null) {
      formatter.SetStatementIndentation(this.Body);
    }

    return true;
  }

  /// <summary>
  /// Assumes type parameters have already been pushed
  /// </summary>
  public void Resolve(ModuleResolver resolver) {
    Contract.Requires(this != null);
    Contract.Requires(resolver.AllTypeConstraints.Count == 0);
    Contract.Ensures(resolver.AllTypeConstraints.Count == 0);

    try {
      resolver.currentMethod = this;

      // make note of the warnShadowing attribute
      bool warnShadowingOption = resolver.Options.WarnShadowing;  // save the original warnShadowing value
      bool warnShadowing = true;
      if (Attributes.ContainsBool(Attributes, "warnShadowing", ref warnShadowing)) {
        resolver.Options.WarnShadowing = warnShadowing;  // set the value according to the attribute
      }

      // Add in-parameters to the scope, but don't care about any duplication errors, since they have already been reported
      resolver.scope.PushMarker();
      if (IsStatic || this is Constructor) {
        resolver.scope.AllowInstance = false;
      }
      foreach (Formal p in Ins) {
        resolver.scope.Push(p.Name, p);
      }

      resolver.ResolveParameterDefaultValues(Ins, new ResolutionContext(this, this is TwoStateLemma));

      // Start resolving specification...
      foreach (AttributedExpression e in Req) {
        resolver.ResolveAttributes(e, new ResolutionContext(this, this is TwoStateLemma));
        resolver.ResolveExpression(e.E, new ResolutionContext(this, this is TwoStateLemma));
        Contract.Assert(e.E.Type != null);  // follows from postcondition of ResolveExpression
        resolver.ConstrainTypeExprBool(e.E, "Precondition must be a boolean (got {0})");
      }

      resolver.ResolveAttributes(Reads, new ResolutionContext(this, false));
      foreach (FrameExpression fe in Reads.Expressions) {
        resolver.ResolveFrameExpressionTopLevel(fe, FrameExpressionUse.Reads, this);
      }

      resolver.ResolveAttributes(Mod, new ResolutionContext(this, false));
      foreach (FrameExpression fe in Mod.Expressions) {
        resolver.ResolveFrameExpressionTopLevel(fe, FrameExpressionUse.Modifies, this);
      }

      resolver.ResolveAttributes(Decreases, new ResolutionContext(this, false));
      foreach (Expression e in Decreases.Expressions) {
        resolver.ResolveExpression(e, new ResolutionContext(this, this is TwoStateLemma));
        // any type is fine
      }

      if (this is Constructor) {
        resolver.scope.PopMarker();
        // start the scope again, but this time allowing instance
        resolver.scope.PushMarker();
        foreach (Formal p in Ins) {
          resolver.scope.Push(p.Name, p);
        }
      }

      // Add out-parameters to a new scope that will also include the outermost-level locals of the body
      // Don't care about any duplication errors among the out-parameters, since they have already been reported
      resolver.scope.PushMarker();
      if (this is ExtremeLemma && Outs.Count != 0) {
        resolver.reporter.Error(MessageSource.Resolver, Outs[0].tok, "{0}s are not allowed to have out-parameters", WhatKind);
      } else {
        foreach (Formal p in Outs) {
          resolver.scope.Push(p.Name, p);
        }
      }

      // ... continue resolving specification
      foreach (AttributedExpression e in Ens) {
        resolver.ResolveAttributes(e, new ResolutionContext(this, true));
        resolver.ResolveExpression(e.E, new ResolutionContext(this, true));
        Contract.Assert(e.E.Type != null);  // follows from postcondition of ResolveExpression
        resolver.ConstrainTypeExprBool(e.E, "Postcondition must be a boolean (got {0})");
      }

      resolver.SolveAllTypeConstraints(); // solve type constraints for specification

      // Resolve body
      if (Body != null) {
        if (this is ExtremeLemma com && com.PrefixLemma != null) {
          // The body may mentioned the implicitly declared parameter _k.  Throw it into the
          // scope before resolving the body.
          var k = com.PrefixLemma.Ins[0];
          resolver.scope.Push(k.Name, k);  // we expect no name conflict for _k
        }

        resolver.DominatingStatementLabels.PushMarker();
        foreach (var req in Req) {
          if (req.Label != null) {
            if (resolver.DominatingStatementLabels.Find(req.Label.Name) != null) {
              resolver.reporter.Error(MessageSource.Resolver, req.Label.Tok, "assert label shadows a dominating label");
            } else {
              var rr = resolver.DominatingStatementLabels.Push(req.Label.Name, req.Label);
              Contract.Assert(rr == Scope<Label>.PushResult.Success);  // since we just checked for duplicates, we expect the Push to succeed
            }
          }
        }

        resolver.ResolveBlockStatement(Body, ResolutionContext.FromCodeContext(this));
        resolver.DominatingStatementLabels.PopMarker();
        resolver.SolveAllTypeConstraints();
      }

      // attributes are allowed to mention both in- and out-parameters (including the implicit _k, for greatest lemmas)
      resolver.ResolveAttributes(this, new ResolutionContext(this, this is TwoStateLemma), true);

      resolver.Options.WarnShadowing = warnShadowingOption; // restore the original warnShadowing value
      resolver.scope.PopMarker();  // for the out-parameters and outermost-level locals
      resolver.scope.PopMarker();  // for the in-parameters

    }
    finally {
      resolver.currentMethod = null;
    }
  }

  public string GetTriviaContainingDocstring() {
    IToken lastClosingParenthesis = null;
    foreach (var token in OwnedTokens) {
      if (token.val == ")") {
        lastClosingParenthesis = token;
      }
    }

    if (lastClosingParenthesis != null && lastClosingParenthesis.TrailingTrivia.Trim() != "") {
      return lastClosingParenthesis.TrailingTrivia;
    }

    return GetTriviaContainingDocstringFromStartTokenOrNull();
  }

  public virtual DafnySymbolKind Kind => DafnySymbolKind.Method;
  public string GetDescription(DafnyOptions options) {
    var qualifiedName = GetQualifiedName();
    var signatureWithoutReturn = $"{WhatKind} {qualifiedName}({string.Join(", ", Ins.Select(i => i.AsText()))})";
    if (Outs.Count == 0) {
      return signatureWithoutReturn;
    }
    return $"{signatureWithoutReturn} returns ({string.Join(", ", Outs.Select(o => o.AsText()))})";
  }

  protected virtual string GetQualifiedName() {
    return $"{AstExtensions.GetMemberQualification(this)}{Name}";
  }

  public IEnumerable<ISymbol> ChildSymbols {
    get {
      IEnumerable<INode> childStatements = Body?.Visit(node => node is Statement) ?? Enumerable.Empty<INode>();
      return Outs.Concat(childStatements.OfType<VarDeclStmt>().SelectMany(v => (IEnumerable<ISymbol>)v.Locals));
    }
  }

  public bool ShouldVerify => true; // This could be made more accurate
  public ModuleDefinition ContainingModule => EnclosingClass.EnclosingModuleDefinition;

  public void AutoRevealDependencies(AutoRevealFunctionDependencies Rewriter, DafnyOptions Options,
    ErrorReporter Reporter) {
    if (Body is null) {
      return;
    }

    object autoRevealDepsVal = null;
    bool autoRevealDeps = Attributes.ContainsMatchingValue(Attributes, "autoRevealDependencies",
      ref autoRevealDepsVal, new List<Attributes.MatchingValueOption> {
        Attributes.MatchingValueOption.Bool,
        Attributes.MatchingValueOption.Int
      }, s => Reporter.Error(MessageSource.Rewriter, ErrorLevel.Error, Tok, s));

    // Default behavior is reveal all dependencies
    int autoRevealDepth = int.MaxValue;

    if (autoRevealDeps) {
      if (autoRevealDepsVal is false) {
        autoRevealDepth = 0;
      } else if (autoRevealDepsVal is BigInteger i) {
        autoRevealDepth = (int)i;
      }
    }

    var currentClass = EnclosingClass;
    List<AutoRevealFunctionDependencies.RevealStmtWithDepth> addedReveals = new();

    foreach (var func in Rewriter.GetEnumerator(this, currentClass, SubExpressions)) {
      var revealStmt =
        AutoRevealFunctionDependencies.BuildRevealStmt(func.Function, Tok, EnclosingClass.EnclosingModuleDefinition);

      if (revealStmt is not null) {
        addedReveals.Add(new AutoRevealFunctionDependencies.RevealStmtWithDepth(revealStmt, func.Depth));
      }
    }

    if (autoRevealDepth > 0) {
      Expression reqExpr = new LiteralExpr(Tok, true) {
        Type = Type.Bool
      };

      foreach (var revealStmt in addedReveals) {
        if (revealStmt.Depth <= autoRevealDepth) {
          if (this is Constructor c) {
            c.BodyInit.Insert(0, revealStmt.RevealStmt);
          } else {
            Body.Body.Insert(0, revealStmt.RevealStmt);
          }

          reqExpr = new StmtExpr(reqExpr.tok, revealStmt.RevealStmt, reqExpr) {
            Type = Type.Bool
          };
        } else {
          break;
        }
      }

      if (Req.Any() || Ens.Any()) {
        Req.Insert(0, new AttributedExpression(reqExpr));
      }
    }

    if (addedReveals.Any()) {
      Reporter.Message(MessageSource.Rewriter, ErrorLevel.Info, null, tok,
        AutoRevealFunctionDependencies.GenerateMessage(addedReveals, autoRevealDepth));
    }
  }
}
