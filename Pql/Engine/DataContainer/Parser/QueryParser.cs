using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using Irony.Parsing;
using Pql.ClientDriver.Protocol;
using Pql.Engine.Interfaces.Internal;
using Pql.Engine.Interfaces.Parsing;
using Pql.ExpressionEngine.Compiler;
using Pql.ExpressionEngine.Interfaces;

namespace Pql.Engine.DataContainer.Parser
{
    internal class QueryParser
    {
        private static readonly Grammar Grammar;
        private static readonly LanguageData LangData;
        private static readonly NonTerminal PqlNonTerminal;

        private static readonly IExpressionEvaluatorRuntime s_expressionRuntime;
        
        private readonly ObjectPool<Irony.Parsing.Parser> m_parsers;
        private readonly DataContainerDescriptor m_containerDescriptor;
        private readonly QueryPreprocessor m_preprocessor;
        private readonly Dictionary<int, ParseTreeNode> m_simpleFieldAccessorNodes;
        
        static QueryParser()
        {
            Grammar = new GrammarPql();
            LangData = new LanguageData(Grammar);
            PqlNonTerminal = LangData.GrammarData.NonTerminals.Single(x => x.Name == "stmtList");
            s_expressionRuntime = new ExpressionEvaluatorRuntime();
            InitPqlExpressionRuntime(s_expressionRuntime);
        }

        private static void InitPqlExpressionRuntime(IExpressionEvaluatorRuntime runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }

            runtime.RegisterDynamicAtomHandler(new AtomMetadata(AtomType.Identifier, "Pql top-level fields", TopLevelAtomExpressionGeneratorForFields));
            runtime.RegisterDynamicAtomHandler(new AtomMetadata(AtomType.Identifier, "Pql parameters", TopLevelAtomExpressionGeneratorForParameters));
            runtime.RegisterAtom(new AtomMetadata(AtomType.Function, "rownum", PredefinedAtom_RowNumber));
            runtime.RegisterAtom(new AtomMetadata(AtomType.Function, "rownumoutput", PredefinedAtom_RowNumberInPage));
        }

        private static Expression PredefinedAtom_RowNumberInPage(ParseTreeNode root, CompilerState compilerState)
        {
            var funArgs = ExpressionTreeExtensions.UnwindTupleExprList(root.RequireChild("funArgs", 1));
            funArgs.RequireChildren(0);

            return Expression.Field(compilerState.Context, "RowNumberInOutput");
        }

        private static Expression PredefinedAtom_RowNumber(ParseTreeNode root, CompilerState compilerState)
        {
            var funArgs = ExpressionTreeExtensions.UnwindTupleExprList(root.RequireChild("funArgs", 1));
            funArgs.RequireChildren(0);

            return Expression.Field(compilerState.Context, "RowNumber");
        }

        private static Expression TopLevelAtomExpressionGeneratorForParameters(ParseTreeNode root, CompilerState compilerState)
        {
            root.RequireChildren(0);
            var pqlCompilerState = (PqlCompilerState) compilerState;
            var parsedRequest = pqlCompilerState.ParsedRequest;

            var names = pqlCompilerState.RequestParameters.Names;
            var paramName = root.Token.ValueString;
            if (string.IsNullOrEmpty(paramName) || paramName[0] != '@')
            {
                // convention is for handlers to return nulls for unknown atoms
                return null;
            }

            if (names != null)
            {
                for (var ordinal = 0; ordinal < names.Length; ordinal++)
                {
                    if (0 == StringComparer.OrdinalIgnoreCase.Compare(names[ordinal], paramName))
                    {
                        return GetOrAddParameterRefToCompilationContext(parsedRequest, pqlCompilerState, ordinal);
                    }
                }
            }

            throw new CompilationException("Undefined parameter: " + paramName, root);
        }

        private static Expression TopLevelAtomExpressionGeneratorForFields(ParseTreeNode root, CompilerState compilerState)
        {
            root.RequireChildren(0);
            var pqlCompilerState = (PqlCompilerState) compilerState;
            var descriptor = pqlCompilerState.ContainerDescriptor;
            var parsedRequest = pqlCompilerState.ParsedRequest;

            var fieldName = root.Token.ValueString;
            var field = descriptor.TryGetField(parsedRequest.TargetEntity.DocumentType, fieldName);
            if (field == null)
            {
                // convention is for handlers to return nulls for unknown atoms
                return null;
            }

            return GetOrAddFieldRefToCompilationContext(parsedRequest, pqlCompilerState, field);
        }

        private static Expression GetOrAddFieldRefToCompilationContext(ParsedRequest parsedRequest, PqlCompilerState compilerState, FieldMetadata field)
        {
            Tuple<ParameterExpression, Expression> refTuple;
            if (compilerState.FieldRefs.TryGetValue(field.FieldId, out refTuple))
            {
                return refTuple.Item1;
            }

            var ordinal = GetFieldOrdinalInDriverFetchFields(parsedRequest, field);
            var rowData = Expression.Field(compilerState.Context, "InputRow");

            var fieldAccessor = DriverRowData.CreateReadAccessor(rowData, field.DbType, ordinal);
            var fieldRef = Expression.Variable(fieldAccessor.Type);

            compilerState.FieldRefs.Add(
                field.FieldId, new Tuple<ParameterExpression, Expression>(fieldRef, fieldAccessor));
            return fieldRef;
        }

        private static Expression GetOrAddParameterRefToCompilationContext(ParsedRequest parsedRequest, PqlCompilerState compilerState, int parameterOrdinal)
        {
            Tuple<ParameterExpression, Expression> refTuple;
            if (compilerState.ParamRefs.TryGetValue(parameterOrdinal, out refTuple))
            {
                return refTuple.Item1;
            }

            ParameterExpression paramRef;
            Expression paramExtractor;
            var localOrdinal = parsedRequest.Params.OrdinalToLocalOrdinal[parameterOrdinal];
            var dbType = parsedRequest.Params.DataTypes[parameterOrdinal];

            if (BitVector.Get(compilerState.RequestParameters.IsCollectionFlags, parameterOrdinal))
            {
                var rowData = Expression.Field(compilerState.Context, "InputParametersCollections");
                var hashsetType = typeof (HashSet<>).MakeGenericType(DriverRowData.DeriveSystemType(dbType));
                paramExtractor = Expression.Convert(Expression.ArrayIndex(rowData, Expression.Constant(localOrdinal, typeof(int))), hashsetType);
                paramRef = Expression.Variable(paramExtractor.Type);
            }
            else
            {
                var rowData = Expression.Field(compilerState.Context, "InputParametersRow");
                paramExtractor = DriverRowData.CreateReadAccessor(rowData, dbType, localOrdinal);
                paramRef = Expression.Variable(paramExtractor.Type);
            }

            compilerState.ParamRefs.Add(
                parameterOrdinal, new Tuple<ParameterExpression, Expression>(paramRef, paramExtractor));
            return paramRef;
        }

        private static int GetFieldOrdinalInDriverFetchFields(ParsedRequest parsedRequest, FieldMetadata field)
        {
            // get ordinal of this field in the dataset returned by storage driver
            var ordinal = parsedRequest.BaseDataset.BaseFields.IndexOf(field);
            if (ordinal < 0)
            {
                throw new Exception(
                    string.Format(
                        "Internal error: driver fetch fields does have have field {0}, id {1} of entity {2}"
                        , field.Name, field.FieldId, parsedRequest.TargetEntity.Name));
            }
            return ordinal;
        }

        private static void InitDriverFetchFields(ParsedRequest parsedRequest)
        {
            if (parsedRequest.IsBulk)
            {
                foreach (var field in parsedRequest.BulkInput.BulkInputFields)
                {
                    parsedRequest.BaseDataset.BaseFields.Add(field);
                }

                parsedRequest.BaseDataset.BaseFieldsMainCount = parsedRequest.BaseDataset.BaseFields.Count;
            }
            else
            {
                foreach (var field in parsedRequest.BaseDataset.WhereClauseFields)
                {
                    parsedRequest.BaseDataset.BaseFields.Add(field);
                }

                foreach (var field in parsedRequest.Select.SelectFields)
                {
                    if (!parsedRequest.BaseDataset.BaseFields.Contains(field))
                    {
                        parsedRequest.BaseDataset.BaseFields.Add(field);
                    }
                }

                parsedRequest.BaseDataset.BaseFieldsMainCount = parsedRequest.BaseDataset.WhereClauseFields.Count;
            }
        }

        /// <summary>
        /// Ctr.
        /// </summary>
        public QueryParser(DataContainerDescriptor containerDescriptor, int maxConcurrency)
        {
            if (containerDescriptor == null)
            {
                throw new ArgumentNullException("containerDescriptor");
            }

            m_parsers = new ObjectPool<Irony.Parsing.Parser>(maxConcurrency, null);
            for (var i = 0; i < maxConcurrency; i++)
            {
                m_parsers.Return(new Irony.Parsing.Parser(LangData, PqlNonTerminal));
            }

            m_containerDescriptor = containerDescriptor;
            m_preprocessor = new QueryPreprocessor(containerDescriptor);

            // these predefined instances of ParseTreeNode are substituted when parsing "select * from .." statement,
            // in order to avoid allocating them every time
            m_simpleFieldAccessorNodes = new Dictionary<int, ParseTreeNode>();
            foreach (var field in m_containerDescriptor.EnumerateFields())
            {
                // generate columnItem -> columnSource -> Id -> id_simple hierarchy, exactly same structure as it comes out of grammar-based parser
                var idNode = new ParseTreeNode(new NonTerminal("Id"), new SourceSpan());
                idNode.ChildNodes.Add(new ParseTreeNode(new Token(new Terminal("id_simple"), new SourceLocation(), field.Name, field.Name)));

                var columnSourceNode = new ParseTreeNode(new NonTerminal("columnSource"), new SourceSpan());
                columnSourceNode.ChildNodes.Add(idNode);

                var columnItemNode = new ParseTreeNode(new NonTerminal("columnItem"), new SourceSpan());
                columnItemNode.ChildNodes.Add(columnSourceNode);
                
                m_simpleFieldAccessorNodes.Add(field.FieldId, columnItemNode);
            }
        }

        public static object CompileWhereClause(DataContainerDescriptor containerDescriptor, ParseTreeNode parseTreeNode, RequestExecutionContextCacheInfo cacheInfo)
        {
            return CompileClause(parseTreeNode, containerDescriptor, cacheInfo, typeof (bool));
        }

        public static PqlCompilerState PrepareCompilerState(DataContainerDescriptor containerDescriptor, RequestExecutionContextCacheInfo cacheInfo, Type returnType)
        {
            return new PqlCompilerState(
                s_expressionRuntime,
                cacheInfo.ParsedRequest,
                cacheInfo.RequestParams,
                containerDescriptor, 
                typeof(ClauseEvaluationContext), returnType);
        }
        
        public static object CompileClause(ParseTreeNode parseTreeNode, DataContainerDescriptor containerDescriptor, RequestExecutionContextCacheInfo cacheInfo, Type returnType)
        {
            var compilerState = PrepareCompilerState(containerDescriptor, cacheInfo, returnType);
            var exprBody = CompileClause(compilerState, parseTreeNode, containerDescriptor, cacheInfo, returnType);
            return s_expressionRuntime.Compile(exprBody, compilerState);
        }

        public static object CompileClause(Expression expression, PqlCompilerState compilerState)
        {
            return s_expressionRuntime.Compile(expression, compilerState);
        }

        public static Expression CompileClause(PqlCompilerState compilerState, ParseTreeNode parseTreeNode, DataContainerDescriptor containerDescriptor, RequestExecutionContextCacheInfo cacheInfo, Type returnType)
        {
            var exprBody = s_expressionRuntime.Analyze(parseTreeNode, compilerState);
            
            // now add our field references to the expression
            if (compilerState.FieldRefs.Count > 0 || compilerState.ParamRefs.Count > 0)
            {
                // variable declarations
                var localVariables = new ParameterExpression[compilerState.FieldRefs.Count + compilerState.ParamRefs.Count];
                var exprList = new Expression[1 + localVariables.Length];

                var ix = 0;
                foreach (var pair in compilerState.FieldRefs)
                {
                    localVariables[ix] = pair.Value.Item1;
                    exprList[ix] = Expression.Assign(pair.Value.Item1, pair.Value.Item2);
                    ix++;
                }
                foreach (var pair in compilerState.ParamRefs)
                {
                    localVariables[ix] = pair.Value.Item1;
                    exprList[ix] = Expression.Assign(pair.Value.Item1, pair.Value.Item2);
                    ix++;
                }

                // and the expression code itself
                exprList[ix] = exprBody;
                // ready to go
                exprBody = Expression.Block(localVariables, exprList);
            }

            return s_expressionRuntime.AdjustReturnType(exprBody, returnType);
        }

        public static object CompileFieldValueExtractorClause(FieldMetadata field, DataContainerDescriptor containerDescriptor, RequestExecutionContextCacheInfo cacheInfo, Type returnType)
        {
            var compilerState = PrepareCompilerState(containerDescriptor, cacheInfo, returnType);
            var fieldAccessor = CompileFieldValueExtractorClause(compilerState, field, containerDescriptor, cacheInfo, returnType);
            return s_expressionRuntime.Compile(fieldAccessor, compilerState);
        }

        public static Expression CompileFieldValueExtractorClause(PqlCompilerState compilerState, FieldMetadata field, DataContainerDescriptor containerDescriptor, RequestExecutionContextCacheInfo cacheInfo, Type returnType)
        {
            var ordinal = GetFieldOrdinalInDriverFetchFields(cacheInfo.ParsedRequest, field);
            var rowData = Expression.Field(compilerState.Context, "InputRow");
            return s_expressionRuntime.AdjustReturnType(DriverRowData.CreateReadAccessor(rowData, field.DbType, ordinal), returnType);
        }

        /// <summary>
        /// Parses specified expression text, with a cancellation option. 
        /// May incur some waiting in highly concurrent environment, because the number of pooled parsers is limited.
        /// </summary>
        public void Parse(DataRequest request, DataRequestBulk requestBulk, ParsedRequest parsedRequest, CancellationToken cancellation)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (parsedRequest == null)
            {
                throw new ArgumentNullException("parsedRequest");
            }

            if (cancellation == null)
            {
                throw new ArgumentNullException("cancellation");
            }

            if (request.HaveRequestBulk && requestBulk == null)
            {
                throw new ArgumentNullException("requestBulk");
            }

            if (request.HaveRequestBulk)
            {
                ParseBulkRequest(requestBulk, parsedRequest);
            }
            else
            {
                ParsePqlStatementRequest(request, parsedRequest, cancellation);
            }

            InitDriverFetchFields(parsedRequest);
        }

        private void ParseBulkRequest(DataRequestBulk requestBulk, ParsedRequest parsedRequest)
        {
            switch (requestBulk.DbStatementType)
            {
                case StatementType.Insert:
                case StatementType.Update:
                    parsedRequest.StatementType = requestBulk.DbStatementType;
                    break;

                default:
                    throw new ArgumentOutOfRangeException("requestBulk", requestBulk.DbStatementType, "Invalid bulk statement type");
            }

            parsedRequest.TargetEntity = m_containerDescriptor.RequireDocumentType(
                m_containerDescriptor.RequireDocumentTypeName(requestBulk.EntityName));

            if (string.IsNullOrEmpty(parsedRequest.TargetEntity.PrimaryKeyFieldName))
            {
                throw new Exception("Target entity does not have a primary key, cannot perform bulk operations on it");
            }

            parsedRequest.TargetEntityPkField = m_containerDescriptor.RequireField(
                parsedRequest.TargetEntity.DocumentType, parsedRequest.TargetEntity.PrimaryKeyFieldName);

            // we always expect value of primary key into driver row data for bulk requests at first position
            parsedRequest.OrdinalOfPrimaryKey = 0;

            if (0 != StringComparer.OrdinalIgnoreCase.Compare(requestBulk.FieldNames[0], parsedRequest.TargetEntityPkField.Name))
            {
                throw new Exception("First field in bulk request input schema on this entity must be the primary key field");
            }

            for (var ordinal = 0; ordinal < requestBulk.FieldNames.Length; ordinal++)
            {
                var fieldName = requestBulk.FieldNames[ordinal];
                var field = m_containerDescriptor.RequireField(parsedRequest.TargetEntity.DocumentType, fieldName);

                if (ordinal != 0 && ReferenceEquals(parsedRequest.TargetEntityPkField, field))
                {
                    throw new Exception("Primary key field may only be used in first position");
                }

                if (parsedRequest.Modify.ModifiedFields.Contains(field))
                {
                    throw new CompilationException("A field can be assigned only once: " + field.Name, null);
                }

                parsedRequest.BulkInput.BulkInputFields.Add(field);
                parsedRequest.Modify.ModifiedFields.Add(field);
                parsedRequest.Modify.InsertUpdateSetClauses.Add(null);
            }
        }

        private void ParsePqlStatementRequest(DataRequest request, ParsedRequest parsedRequest, CancellationToken cancellation)
        {
            ParseTree parseTree;
            using (var poolAccessor = m_parsers.Take(cancellation))
            {
                try
                {
                    parseTree = poolAccessor.Item.Parse(request.CommandText);
                }
                finally
                {
                    // get rid of temp utility objects right now, to help them be reclaimed with Gen0
                    // poolAccessor.Item.Reset();
                }
            }

            if (parseTree.Status != ParseTreeStatus.Parsed)
            {
                throw new CompilationException(BuildParserErrorMessage(parseTree));
            }

            // root is a batch of Pql statements
            var root = parseTree.Root;

            if (root.ChildNodes == null || root.ChildNodes.Count != 1)
            {
                throw new CompilationException("Pql batch must contain exactly one statement", root);
            }

            var statementRoot = root.ChildNodes[0];

            // run first round of syntactical analysis on the tree
            if ("selectStmt".Equals(statementRoot.Term.Name))
            {
                parsedRequest.StatementType = StatementType.Select;
                ParseSelectStatement(parsedRequest, statementRoot);
            }
            else if ("updateStmt".Equals(statementRoot.Term.Name))
            {
                parsedRequest.StatementType = StatementType.Update;
                ParseUpdateStatement(parsedRequest, statementRoot);
            }
            else if ("insertStmt".Equals(statementRoot.Term.Name))
            {
                parsedRequest.StatementType = StatementType.Insert;
                ParseInsertStatement(parsedRequest, statementRoot);
            }
            else if ("deleteStmt".Equals(statementRoot.Term.Name))
            {
                parsedRequest.StatementType = StatementType.Delete;
                ParseDeleteStatement(parsedRequest, statementRoot);
            }
            else
            {
                throw new CompilationException("Invalid statement: " + statementRoot.Term.Name, statementRoot);
            }
        }

        private void ParseInsertStatement(ParsedRequest parsedRequest, ParseTreeNode insertStmt)
        {
            // get FROM entity name
            var insertEntityClause = insertStmt.RequireChild("Id", 2);
            parsedRequest.TargetEntity = GetTargetEntity(insertEntityClause);

            // preprocess identifiers
            m_preprocessor.ProcessIdentifierAliases(insertStmt, parsedRequest.TargetEntity);

            var insertFieldsListClause = insertStmt.RequireChild("idList", 3, 0);
            var valueListClause = insertStmt.RequireChild("insertTuplesList", 4, 1);

            if (valueListClause.ChildNodes.Count != 1)
            {
                throw new CompilationException("Multi-tuple explicit list of values for INSERT is not yet supported");
            }

            valueListClause = valueListClause.ChildNodes[0];
            
            if (insertFieldsListClause.ChildNodes.Count != valueListClause.ChildNodes.Count)
            {
                throw new CompilationException("Number of fields in INSERT clause must match number of expressions in VALUES clause", insertStmt);
            }

            parsedRequest.TargetEntityPkField = m_containerDescriptor.RequireField(parsedRequest.TargetEntity.DocumentType, parsedRequest.TargetEntity.PrimaryKeyFieldName);
            for (var ordinal = 0; ordinal < insertFieldsListClause.ChildNodes.Count; ordinal++)
            {
                var insertFieldNode = insertFieldsListClause.ChildNodes[ordinal];
                var field = TryGetFieldByIdentifierNode(insertFieldNode, m_containerDescriptor, parsedRequest.TargetEntity.DocumentType);
                if (field == null)
                {
                    throw new CompilationException("Attempting to INSERT into an unknown field", insertFieldNode);
                }

                if (ReferenceEquals(field, parsedRequest.TargetEntityPkField))
                {
                    parsedRequest.OrdinalOfPrimaryKey = ordinal;
                }

                if (parsedRequest.Modify.ModifiedFields.Contains(field))
                {
                    throw new CompilationException("A field can be assigned by INSERT only once: " + field.Name, insertFieldNode);
                }

                var expressionClause = valueListClause.ChildNodes[ordinal];

                // update clauses list will hold value expressions
                parsedRequest.Modify.ModifiedFields.Add(field);
                parsedRequest.Modify.InsertUpdateSetClauses.Add(expressionClause);
            }

            if (parsedRequest.OrdinalOfPrimaryKey < 0)
            {
                throw new CompilationException("Value for primary key field must be specified: " + parsedRequest.TargetEntityPkField.Name);
            }
        }

        private void ParseDeleteStatement(ParsedRequest parsedRequest, ParseTreeNode deleteStmt)
        {
            // get FROM entity name
            var deleteFromClause = deleteStmt.RequireChild("Id", 2);
            parsedRequest.TargetEntity = GetTargetEntity(deleteFromClause);

            // preprocess identifiers
            m_preprocessor.ProcessIdentifierAliases(deleteStmt, parsedRequest.TargetEntity);

            var ctx = GetTreeIteratorContext(parsedRequest, m_containerDescriptor);
            ctx.Functor = FieldExtractor;

            // get field names for where clause
            var whereClause = deleteStmt.TryGetChild("whereClauseOpt", 3);
            ParseWhereClause(whereClause, ctx);

            // get field names for order clause
            ctx.ParsedRequest.BaseDataset.OrderClause = deleteStmt.TryGetChild("orderList", 4, 2);
            ParseOrderClause(ctx);
        }

        private void ParseUpdateStatement(ParsedRequest parsedRequest, ParseTreeNode updateStmt)
        {
            // get FROM entity name
            var updateEntityClause = updateStmt.RequireChild("Id", 1);
            parsedRequest.TargetEntity = GetTargetEntity(updateEntityClause);

            // preprocess identifiers
            m_preprocessor.ProcessIdentifierAliases(updateStmt, parsedRequest.TargetEntity);

            var ctx = GetTreeIteratorContext(parsedRequest, m_containerDescriptor);
            ctx.Functor = FieldExtractor;

            var assignListClause = updateStmt.RequireChild("assignList", 3);
            foreach (var assignClause in assignListClause.ChildNodes)
            {
                var field = TryGetFieldByIdentifierNode(assignClause.RequireChild("Id", 0), m_containerDescriptor, parsedRequest.TargetEntity.DocumentType);
                if (field == null)
                {
                    throw new CompilationException("Attempting to SET an unknown field", assignClause);
                }

                if (parsedRequest.Modify.ModifiedFields.Contains(field))
                {
                    throw new CompilationException("A field can be assigned by UPDATE only once: " + field.Name, null);
                }

                var expressionClause = assignClause.RequireChild(null, 2);
                parsedRequest.Modify.ModifiedFields.Add(field);
                parsedRequest.Modify.InsertUpdateSetClauses.Add(expressionClause);

                // iterator callback will place all referenced column IDs into "select" list
                // later on, "select" list is used along with "where" list to create driver fetch list
                ctx.Argument = 1;
                IterateTree(expressionClause, 0, ctx);
            }

            // get field names for where clause
            var whereClause = updateStmt.TryGetChild("whereClauseOpt", 4);
            ParseWhereClause(whereClause, ctx);

            // get field names for order clause
            ctx.ParsedRequest.BaseDataset.OrderClause = updateStmt.TryGetChild("orderList", 5, 2);
            ParseOrderClause(ctx);
        }

        private void ParseSelectStatement(ParsedRequest parsedRequest, ParseTreeNode selectStmt)
        {
            // get FROM entity name
            var fromClause = selectStmt.RequireChild("fromClauseOpt", 4);
            var fromEntityNode = fromClause.RequireChild("Id", 1, 0);
            parsedRequest.TargetEntity = GetTargetEntity(fromEntityNode);

            var ctx = GetTreeIteratorContext(parsedRequest, m_containerDescriptor);
            ctx.Functor = FieldExtractor;

            // get field names for select clause
            var selectColumnItemList = selectStmt.TryGetChild(null, 2, 0);
            if (selectColumnItemList == null)
            {
                throw new CompilationException("Could not find select clause", selectStmt);
            }

            if (0 == StringComparer.Ordinal.Compare("columnItemList", selectColumnItemList.Term.Name))
            {
                // iterator callback will place all referenced column IDs into "select" list
                // later on, "select" list is used along with "where" list to create driver fetch list
                ctx.Argument = 1;

                foreach (var columnItem in selectColumnItemList.ChildNodes)
                {
                    // column or column with alias
                    columnItem.RequireChildren(1, 2);

                    var columnSource = columnItem.RequireChild("columnSource", 0);

                    // store a reference to every column item and its alias, 
                    // since we may need to compile it into an expression or do something else later 
                    parsedRequest.Select.SelectClauses.Add(columnItem);

                    // preprocess identifiers
                    m_preprocessor.ProcessIdentifierAliases(columnSource, parsedRequest.TargetEntity);

                    // extract field names from a regular select clause (but do not look into "as" alias)
                    IterateTree(columnSource, 0, ctx);
                }
            }
            else if (selectColumnItemList.ChildNodes.Count == 0 && selectColumnItemList.Token != null && "*".Equals(selectColumnItemList.Token.Text))
            {
                // they ask for all fields (wildcard)
                // let's extract only unpacked blobs
                var docTypeDescriptor = parsedRequest.TargetEntity;
                foreach (var field in m_containerDescriptor.EnumerateFields().Where(x => x.OwnerDocumentType == docTypeDescriptor.DocumentType))
                {
                    ctx.ParsedRequest.Select.SelectFields.Add(field);
                    ctx.ParsedRequest.Select.SelectClauses.Add(m_simpleFieldAccessorNodes[field.FieldId]);
                }
            }
            else
            {
                throw new CompilationException("Must have list of columns or an asterisk in select clause", selectColumnItemList);
            }

            // get field names for where clause
            var whereClause = selectStmt.TryGetChild("whereClauseOpt", 5);
            ParseWhereClause(whereClause, ctx);

            // get field names for order clause
            ctx.ParsedRequest.BaseDataset.OrderClause = selectStmt.TryGetChild("orderList", 8, 2);
            ParseOrderClause(ctx);

            ReadPagingOptions(selectStmt, ctx.ParsedRequest);
        }

        private DocumentTypeDescriptor GetTargetEntity(ParseTreeNode parseTreeNode)
        {
            var targetEntityName = parseTreeNode.RequireChild("id_simple", 0).Token.ValueString;
            try
            {
                var docType = m_containerDescriptor.RequireDocumentTypeName(targetEntityName);
                return m_containerDescriptor.RequireDocumentType(docType);
            }
            catch (ArgumentException)
            {
                throw new CompilationException("Unknown entity: " + targetEntityName, parseTreeNode);
            }
        }

        private void ParseWhereClause(ParseTreeNode whereClause, TreeIteratorContext ctx)
        {
            if (whereClause != null)
            {
                ctx.ParsedRequest.BaseDataset.WhereClauseRoot = whereClause.ChildNodes.Count == 0 ? null : whereClause.RequireChild(null, 1);

                if (ctx.ParsedRequest.BaseDataset.WhereClauseRoot != null)
                {
                    // preprocess identifiers
                    m_preprocessor.ProcessIdentifierAliases(whereClause, ctx.ParsedRequest.TargetEntity);

                    // iterator callback will place column IDs into "where" list
                    ctx.Argument = 2;

                    // extract all field names
                    IterateTree(ctx.ParsedRequest.BaseDataset.WhereClauseRoot, 0, ctx);
                }
            }
        }

        private void ParseOrderClause(TreeIteratorContext ctx)
        {
            if (ctx.ParsedRequest.BaseDataset.OrderClause != null)
            {
                // preprocess identifiers
                m_preprocessor.ProcessIdentifierAliases(ctx.ParsedRequest.BaseDataset.OrderClause, ctx.ParsedRequest.TargetEntity);

                // iterator callback will place column IDs and ASC/DESC flag into "order" list
                ctx.Functor = OrderFieldExtractor;

                // extract all field names
                IterateTree(ctx.ParsedRequest.BaseDataset.OrderClause, 0, ctx);
            }
        }

        private static void ReadPagingOptions(ParseTreeNode selectStmt, ParsedRequest parsedRequest)
        {
            var clause = selectStmt.RequireChild("pageOffset", 10);
            if (clause != null && clause.ChildNodes.Count > 0)
            {
                var param = clause.TryGetChild("Id", 1);
                if (param != null)
                {
                    parsedRequest.BaseDataset.Paging.Offset = CompileInt32ParamExtractorForPaging(parsedRequest, param, 0);
                }
                else
                {
                    var number = clause.RequireChild("number", 1);
                    int value;
                    if (!int.TryParse(number.Token.ValueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                        || value < 0)
                    {
                        throw new CompilationException("Invalid value for page offset", number);
                    }
                    parsedRequest.BaseDataset.Paging.Offset = x => value;
                }
            }

            clause = selectStmt.RequireChild("pageSize", 9);
            if (clause != null && clause.ChildNodes.Count > 0)
            {
                var param = clause.TryGetChild("Id", 1);
                if (param != null)
                {
                    parsedRequest.BaseDataset.Paging.PageSize = CompileInt32ParamExtractorForPaging(parsedRequest, param, Int32.MaxValue);
                }
                else
                {
                    var number = clause.RequireChild("number", 1);
                    int value;
                    if (!int.TryParse(number.Token.ValueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                        || value < -1)
                    {
                        throw new CompilationException("Invalid value for page size", number);
                    }

                    if (value != -1)
                    {
                        parsedRequest.BaseDataset.Paging.PageSize = x => value;
                    }
                }
            }
        }

        private static Func<DriverRowData, int> CompileInt32ParamExtractorForPaging(ParsedRequest parsedRequest, ParseTreeNode parseTreeNode, int defaultValue)
        {
            var paramName = parseTreeNode.RequireChild("id_simple", 0).Token.ValueString;
            if (string.IsNullOrEmpty(paramName) || paramName[0] != '@')
            {
                throw new CompilationException("Values for paging clause must be Int32 constants or Int32 parameters", parseTreeNode);
            }

            var names = parsedRequest.Params.Names;
            if (names != null)
            {
                for (var paramOrdinal = 0; paramOrdinal < names.Length; paramOrdinal++)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(names[paramOrdinal], paramName))
                    {
                        if (parsedRequest.Params.DataTypes[paramOrdinal] != DbType.Int32)
                        {
                            throw new CompilationException(string.Format(
                                "Parameter {0} must be of type Int32 for use in paging options. Actual type: {1}", 
                                paramName, parsedRequest.Params.DataTypes[paramOrdinal])
                            , parseTreeNode);
                        }

                        var localOrdinal = parsedRequest.Params.OrdinalToLocalOrdinal[paramOrdinal];
                        return data => BitVector.Get(data.NotNulls, localOrdinal) ? data.GetInt32(localOrdinal) : defaultValue;
                    }
                }
            }

            throw new CompilationException("Unknown parameter: " + paramName, parseTreeNode);
        }

        private static bool FieldExtractor(TreeIteratorContext ctx, ParseTreeNode node)
        {
            if (0 != StringComparer.Ordinal.Compare(node.Term.Name, "Id"))
            {
                // keep going deeper
                return true;
            }

            var field = TryGetFieldByIdentifierNode(node, ctx.ContainerDescriptor, ctx.ParsedRequest.TargetEntity.DocumentType);
            if (field != null)
            {
                switch (ctx.Argument)
                {
                    case 1:
                        if (!ctx.ParsedRequest.Select.SelectFields.Contains(field))
                        {
                            ctx.ParsedRequest.Select.SelectFields.Add(field);
                        }
                        break;
                    case 2:
                        if (!ctx.ParsedRequest.BaseDataset.WhereClauseFields.Contains(field))
                        {
                            ctx.ParsedRequest.BaseDataset.WhereClauseFields.Add(field);
                        }
                        break;
                    default:
                        throw new Exception("Internal error, invalid argument for iterator: " + ctx.Argument);
                }
            }

            // no need to traverse deeper into the Id non-terminal
            return false;
        }

        private static FieldMetadata TryGetFieldByIdentifierNode(ParseTreeNode node, DataContainerDescriptor containerDescriptor, int docType)
        {
            // we support simple and dot-separated identifiers
            // for a dot-separated identifier, field name is the part before first dot, and it MUST be an object type field
            if (node.ChildNodes.Count >= 1)
            {
                var idsimple = node.ChildNodes[0];
                if (0 == StringComparer.Ordinal.Compare(idsimple.Term.Name, "id_simple"))
                {
                    var fieldName = idsimple.Token.ValueString;
                    var field = containerDescriptor.TryGetField(docType, fieldName);
                    if (field != null)
                    {
                        if (node.ChildNodes.Count > 1)
                        {
                            throw new CompilationException("Dotted notation is supported, but not allowed in Pql server context");
                            //if (field.DbType != DbType.Object)
                            //{
                            //    throw new CompilationException("Dotted notation is only allowed on object-type fields", node);
                            //}
                        }
                    }

                    return field;
                }
            }

            return null;
        }

        private static bool OrderFieldExtractor(TreeIteratorContext ctx, ParseTreeNode node)
        {
            if (0 == StringComparer.Ordinal.Compare(node.Term.Name, "orderMember"))
            {
                var idsimple = node.RequireChild("Id", 0).RequireChild("id_simple", 0);
                var fieldName = idsimple.Token.ValueString;
                var field = ctx.ContainerDescriptor.TryGetField(ctx.ParsedRequest.TargetEntity.DocumentType, fieldName);
                if (field == null)
                {
                    throw new CompilationException("Unknown field: " + fieldName, node);
                }

                var descOpt = node.TryGetChild(null, 1, 0);
                var descending = descOpt != null;
                if (descending)
                {
                    if (0 == StringComparer.Ordinal.Compare(descOpt.Term.Name, "ASC"))
                    {
                        descending = false;
                    }
                    else if (0 != StringComparer.Ordinal.Compare(descOpt.Term.Name, "DESC"))
                    {
                        throw new CompilationException("Invalid sort order option: " + descOpt.Token.Text, descOpt);
                    }
                }

                if (ctx.ParsedRequest.BaseDataset.OrderClauseFields.Count > 0)
                {
                    throw new CompilationException("Cannot sort by more than one field at this time", node);
                }

                if (0 <= ctx.ParsedRequest.BaseDataset.OrderClauseFields.FindIndex(x => x.Item1 == field.FieldId))
                {
                    throw new CompilationException("Duplicate order field: " + field.Name, node);
                }

                ctx.ParsedRequest.BaseDataset.OrderClauseFields.Add(new Tuple<int, bool>(field.FieldId, descending));

                // no need to traverse deeper into the Id non-terminal
                return false;
            }

            return true;
        }

        private static TreeIteratorContext GetTreeIteratorContext(ParsedRequest parsedRequest, DataContainerDescriptor containerDescriptor)
        {
            var result = new TreeIteratorContext();
            result.ContainerDescriptor = containerDescriptor;
            result.ParsedRequest = parsedRequest;
            return result;
        }

        private string BuildParserErrorMessage(ParseTree result)
        {
            var builder = new StringBuilder(2000);
            builder.AppendLine("Failed to parse expression. See parser output below.");
            foreach (var msg in result.ParserMessages)
            {
                builder.AppendFormat("{0} - {1} at {2}: {3}", msg.ParserState, msg.Level,
                    CompilationException.FormatLocationString(msg.Location, -1), msg.Message);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private class TreeIteratorContext
        {
            public int Argument;
            public DataContainerDescriptor ContainerDescriptor;
            public TreeIteratorDelegate Functor;
            public ParsedRequest ParsedRequest;
        }

        private delegate bool TreeIteratorDelegate(TreeIteratorContext ctx, ParseTreeNode node);
        
        private static void IterateTree(ParseTreeNode root, int level, TreeIteratorContext ctx)
        {
            if (!ctx.Functor(ctx, root))
            {
                // no need to go deeper into the tree
                return;
            }
            
            foreach (var child in root.ChildNodes)
            {
                IterateTree(child, level + 1, ctx);
            }
        }

        private static void ReadParsedRequestFromCache()
        {
            
        }

        private static void WriteParsedRequestToCache(RequestExecutionContextCacheInfo cacheInfo, ParsedRequest parsedRequest)
        {

        }

        public static DriverRowData CreateDriverRowDataBuffer(IList<FieldMetadata> fields)
        {
            // field types in the driver buffer must come in particular order 
            var fieldTypes = new DbType[fields.Count];
            for (var i = 0; i < fields.Count; i++)
            {
                fieldTypes[i] = fields[i].DbType;
            }
            return new DriverRowData(fieldTypes);
        }
    }

    public class PqlCompilerState : CompilerState
    {
        public readonly ParsedRequest ParsedRequest;
        public readonly DataRequestParams RequestParameters;
        public readonly DataContainerDescriptor ContainerDescriptor;
        public readonly Dictionary<int, Tuple<ParameterExpression, Expression>> FieldRefs;
        public readonly Dictionary<int, Tuple<ParameterExpression, Expression>> ParamRefs;

        public PqlCompilerState(
            IExpressionEvaluatorRuntime parentRuntime, 
            ParsedRequest parsedRequest, 
            DataRequestParams requestParams,
            DataContainerDescriptor containerDescriptor,
            Type contextType, 
            Type returnType) 
            : base(parentRuntime, contextType, returnType, null)
        {
            if (parsedRequest == null)
            {
                throw new ArgumentNullException("parsedRequest");
            }

            if (containerDescriptor == null)
            {
                throw new ArgumentNullException("containerDescriptor");
            }

            if (requestParams == null)
            {
                throw new ArgumentNullException("requestParams");
            }

            ParsedRequest = parsedRequest;
            RequestParameters = requestParams;
            ContainerDescriptor = containerDescriptor;
            FieldRefs = new Dictionary<int, Tuple<ParameterExpression, Expression>>();
            ParamRefs = new Dictionary<int, Tuple<ParameterExpression, Expression>>();
        }
    }
}
