using System;
using System.Linq.Expressions;
using System.Threading;
using Irony.Parsing;
using Pql.ExpressionEngine.Interfaces;

namespace Pql.ExpressionEngine.Compiler
{
    /// <summary>
    /// Implements compiler runtime.
    /// </summary>
    public partial class ExpressionEvaluatorRuntime : IExpressionEvaluatorRuntime
    {
        /// <summary>
        /// Registers an atom to process identifiers whose names are not known at the time of runtime initialization.
        /// Atom will be used as a handler and must have non-null <see cref="AtomMetadata.ExpressionGenerator"/> member.
        /// This handler will be invoked from inside <see cref="IExpressionEvaluatorRuntime.Analyze"/> for identifiers which are not statically known atoms.
        /// More than one handler can be registered, they will be invoked one after another in arbitrary order 
        /// until one of their expression generators returns a non-null value.
        /// </summary>
        /// <param name="atom">An atom to register. Must have non-null value of <see cref="AtomMetadata.ExpressionGenerator"/></param>
        /// <see cref="IExpressionEvaluatorRuntime.RegisterAtom"/>
        /// <see cref="AtomMetadata.ExpressionGenerator"/>
        public void RegisterDynamicAtomHandler(AtomMetadata atom)
        {
            if (atom == null)
            {
                throw new ArgumentNullException("atom");
            }

            if (atom.AtomType != AtomType.Function && atom.AtomType != AtomType.Identifier)
            {
                throw new ArgumentException("Atom type is invalid: " + atom.AtomType);
            }

            if (atom.ExpressionGenerator == null)
            {
                throw new ArgumentException("Atom handlers must have non-null ExpressionGenerator member", "atom");
            }

            m_atomHandlers.Add(atom);
        }

        /// <summary>
        /// Registers a new atom. This API is for statically known identifiers and functions.
        /// To process identifiers whose names are not known at the time of runtime init use 
        /// <see cref="IExpressionEvaluatorRuntime.RegisterDynamicAtomHandler"/>.
        /// </summary>
        /// <param name="atom">An atom to register</param>
        /// <seealso cref="AtomMetadata"/>
        /// <seealso cref="IExpressionEvaluatorRuntime.RegisterDynamicAtomHandler"/>
        public void RegisterAtom(AtomMetadata atom)
        {
            if (atom == null)
            {
                throw new ArgumentNullException("atom");
            }

            if (atom.AtomType != AtomType.Function && atom.AtomType != AtomType.Identifier)
            {
                throw new ArgumentException("Atom type is invalid: " + atom.AtomType);
            }

            if (!m_atoms.TryAdd(atom.Name, atom))
            {
                throw new ArgumentException("Atom with the same name is already registered: " + atom.Name);
            }
        }

        /// <summary>
        /// Checks if an atom with this name is already registered.
        /// </summary>
        /// <param name="name">Name to look for, case-insensitive</param>
        /// <returns>True if any atom with this name is already registered</returns>
        public bool IsAtomRegistered(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException("name");
            }

            return m_atoms.ContainsKey(name);
        }

        /// <summary>
        /// If return type of the given <paramref name="expression"/> does not match <paramref name="type"/>,
        /// attempts to adjust it. Returns same or new Expression object.
        /// If supplied type is <c>null</c>, expression becomes Action.
        /// </summary>
        /// <param name="expression">Parse tree root</param>
        /// <param name="type">Desired return type, or null to produce an action</param>
        /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null</exception>
        /// <exception cref="CompilationException">Could produce a safe adjustment</exception>
        public Expression AdjustReturnType(Expression expression, Type type)        
        {
            if (expression == null)
            {
                throw new ArgumentNullException("expression");
            }

            return type != null ? ExpressionTreeExtensions.AdjustReturnType(null, expression, type) : expression;
        }

        /// <summary>
        /// Parses, analyzes and compiles given expression string into a CLR lambda of appropriate type (some flavor of Func or Action).
        /// May incur some waiting in highly concurrent environment, because the number of pooled parsers is limited.
        /// </summary>
        /// <param name="text">Expression text</param>
        /// <param name="returnType">Desired return type - used for verification. Supply null to compile to Action</param>
        /// <param name="args">Ordered name-type pairs of input arguments</param>
        /// <returns>Compiled lambda, some flavor of Func or Action</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null</exception>
        /// <exception cref="CompilationException">Compilation errors, check exception details</exception>
        public object Compile(string text, Type returnType, params Tuple<string, Type>[] args)
        {
            return Compile(text, CancellationToken.None, returnType, args);
        }

        /// <summary>
        /// Parses, analyzes and compiles given expression string into a CLR lambda of appropriate type (some flavor of Func or Action).
        /// May incur some waiting in highly concurrent environment, because the number of pooled parsers is limited.
        /// Has cancellation option.
        /// </summary>
        /// <param name="text">Expression text</param>
        /// <param name="cancellation">Cancellation token source to be used to abort waiting. Supply null to wait indefinitely.</param>
        /// <param name="returnType">Desired return type - used for verification. Supply null to compile to Action</param>
        /// <param name="args">Ordered name-type pairs of input arguments</param>
        /// <returns>Compiled lambda, some flavor of Func or Action</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null</exception>
        /// <exception cref="CompilationException">Compilation errors, check exception details</exception>
        public object Compile(string text, CancellationToken cancellation, Type returnType, params Tuple<string, Type>[] args)
        {
            var tree = Parse(text, cancellation);
            var state = new CompilerState(this, null, returnType, args);
            var expression = Analyze(tree, state);
            return CompileImpl(expression, state);
        }

        /// <summary>
        /// Parses, analyzes and compiles given expression string into a CLR lambda of appropriate type (some flavor of Func or Action).
        /// May incur some waiting in highly concurrent environment, because the number of pooled parsers is limited.
        /// </summary>
        /// <param name="text">Expression text</param>
        /// <typeparam name="TContext">Desired input holder type, will be the only input argument. Available in <seealso cref="CompilerState.Arguments"/> by name "Context"</typeparam>
        /// <typeparam name="T">Desired return type, used for verification</typeparam>
        /// <returns>Compiled Func</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null</exception>
        /// <exception cref="CompilationException">Compilation errors, check exception details</exception>
        public Func<TContext, T> Compile<TContext, T>(string text)
        {
            return Compile<TContext, T>(text, CancellationToken.None);
        }

        /// <summary>
        /// Parses, analyzes and compiles given expression string into a CLR lambda of appropriate type (some flavor of Func or Action).
        /// May incur some waiting in highly concurrent environment, because the number of pooled parsers is limited.
        /// Has cancellation option.
        /// </summary>
        /// <param name="text">Expression text</param>
        /// <param name="cancellation">Cancellation token source to be used to abort waiting. Supply null to wait indefinitely.</param>
        /// <typeparam name="TContext">Desired input holder type, will be the only input argument. Available in <seealso cref="CompilerState.Arguments"/> by name "Context"</typeparam>
        /// <typeparam name="T">Desired return type, used for verification</typeparam>
        /// <returns>Compiled Func</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null</exception>
        /// <exception cref="CompilationException">Compilation errors, check exception details</exception>
        public Func<TContext, T> Compile<TContext, T>(string text, CancellationToken cancellation)
        {
            var tree = Parse(text, cancellation);
            var state = new CompilerState(this, typeof(TContext), typeof(T));
            var expression = Analyze(tree, state);
            return (Func<TContext, T>)CompileImpl(expression, state);
        }

        /// <summary>
        /// Given an expression (assuming it was generated with call to Analyze, attempts to compile it into a lambda of some type.
        /// </summary>
        /// <param name="expression">Expression</param>
        /// <param name="state">Compiler state</param>
        /// <returns>Compiled Func</returns>
        /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null</exception>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null</exception>
        /// <exception cref="CompilationException">Mismatch in argument or context types, other consistency errors</exception>
        public object Compile(Expression expression, CompilerState state)
        {
            return CompileImpl(expression, state);
        }

        /// <summary>
        /// Parses specified expression text, with a cancellation option. 
        /// May incur some waiting in highly concurrent environment without factory delegate, because the number of pooled parsers is limited.
        /// </summary>
        /// <param name="text">Expression text</param>
        /// <param name="cancellation">Cancellation token source to be used to abort waiting for parser. Supply <see cref="CancellationToken.None"/> to wait indefinitely.</param>
        /// <returns>Abstract syntax tree</returns>
        /// <exception cref="OperationCanceledException">Waiting for some parser to become available was aborted</exception>
        /// <exception cref="CompilationException">Failed to parse, message contains details from parser</exception>
        public ParseTree Parse(string text, CancellationToken cancellation)
        {
            ParseTree result;
            using (var poolAccessor = m_expressionParsers.Take(cancellation))
            {
                try
                {
                    result = poolAccessor.Item.Parse(text);
                }
                finally
                {
                    //poolAccessor.Item.Reset();
                }
            }

            if (result.Status != ParseTreeStatus.Parsed)
            {
                throw new CompilationException(BuildParserErrorMessage(result));
            }

            return result;
        }

        /// <summary>
        /// Produces .NET Expression object from the given abstract syntax tree.
        /// Supports re-entrancy, useful for expression generators (see <see cref="AtomMetadata.ExpressionGenerator"/>).
        /// </summary>
        /// <param name="root">Parse tree root</param>
        /// <param name="state">Compiler state</param>
        /// <returns>Expression node with verified logical data type</returns>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> is null</exception>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null</exception>
        /// <exception cref="CompilationException">Compilation errors, check exception details</exception>
        public Expression Analyze(ParseTreeNode root, CompilerState state)
        {
            switch (root.Term.Name)
            {
                case "exprList":
                case "tuple":
                    root.RequireChildren(1);
                    var newRoot = ExpressionTreeExtensions.UnwindTupleExprList(root.ChildNodes[0]);
                    return Analyze(newRoot, state);
                case "betweenExpr":
                    return BuildBetweenExpression(root, state);
                case "binExpr":
                    return BuildBinaryExpression(root, state);
                case "unExpr":
                    return BuildUnaryExpression(root, state);
                case "case":
                    return BuildCaseStatementExpression(root, state);
                case "Id":
                    return BuildIdentifierExpression(root, state);
                case "number":
                    return BuildNumericConstantExpression(root, state);
                case "string":
                    return BuildStringLiteralExpression(root, state);
                case "funCall":
                    return BuildFunCallExpression(root, state);
                default:
                    throw new CompilationException("Term not yet supported: " + root.Term.Name, root);
            }
        }
    }
}
