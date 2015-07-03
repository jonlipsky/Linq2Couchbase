﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Couchbase.Linq.Clauses;
using Couchbase.Linq.Extensions;
using Couchbase.Linq.Operators;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;

namespace Couchbase.Linq.QueryGeneration
{
    public class N1QlQueryModelVisitor : QueryModelVisitorBase //: N1QlQueryModelVisitorBase
    {
        private readonly ParameterAggregator _parameterAggregator = new ParameterAggregator();
        private readonly QueryPartsAggregator _queryPartsAggregator = new QueryPartsAggregator();
        private readonly IMethodCallTranslatorProvider _methodCallTranslatorProvider;
        private readonly List<UnclaimedGroupJoin> _unclaimedGroupJoins = new List<UnclaimedGroupJoin>(); 

        private bool _isSubQuery = false;

        public N1QlQueryModelVisitor()
        {
            _methodCallTranslatorProvider = new DefaultMethodCallTranslatorProvider();
        }

        public N1QlQueryModelVisitor(IMethodCallTranslatorProvider methodCallTranslatorProvider)
        {
            if (methodCallTranslatorProvider == null)
            {
                throw new ArgumentNullException("methodCallTranslatorProvider");
            }

            _methodCallTranslatorProvider = methodCallTranslatorProvider;
        }

        public static string GenerateN1QlQuery(QueryModel queryModel)
        {
            var visitor = new N1QlQueryModelVisitor();
            visitor.VisitQueryModel(queryModel);
            return visitor.GetQuery();
        }

        public string GetQuery()
        {
            return _queryPartsAggregator.BuildN1QlQuery();
        }

        public override void VisitQueryModel(QueryModel queryModel)
        {
            queryModel.SelectClause.Accept(this, queryModel);
            queryModel.MainFromClause.Accept(this, queryModel);
            VisitBodyClauses(queryModel.BodyClauses, queryModel);
            VisitResultOperators(queryModel.ResultOperators, queryModel);

            if (_unclaimedGroupJoins.Any())
            {
                throw new NotSupportedException("N1QL Requires All Group Joins Have A Matching From Clause Subquery");
        }
        }

        public override void VisitMainFromClause(MainFromClause fromClause, QueryModel queryModel)
        {
            var bucketConstantExpression = fromClause.FromExpression as ConstantExpression;
            if ((bucketConstantExpression != null) &&
                typeof(IBucketQueryable).IsAssignableFrom(bucketConstantExpression.Type))
            {
                _queryPartsAggregator.AddFromPart(new N1QlFromQueryPart()
                {
                    Source = EscapeIdentifier(((IBucketQueryable) bucketConstantExpression.Value).BucketName),
                    ItemName = EscapeIdentifier(fromClause.ItemName)
                });
            }
            else if (fromClause.FromExpression.NodeType == ExpressionType.MemberAccess)
            {
                _queryPartsAggregator.AddFromPart(new N1QlFromQueryPart()
                {
                    Source = GetN1QlExpression((MemberExpression) fromClause.FromExpression),
                    ItemName = EscapeIdentifier(fromClause.ItemName)
                });

                _isSubQuery = true;
            }

            base.VisitMainFromClause(fromClause, queryModel);
        }

        public override void VisitSelectClause(SelectClause selectClause, QueryModel queryModel)
        {
            _queryPartsAggregator.SelectPart = GetSelectParameters(selectClause, queryModel);
            
            base.VisitSelectClause(selectClause, queryModel);
        }

        private string GetSelectParameters(SelectClause selectClause, QueryModel queryModel)
        {
            string expression;

            if (selectClause.Selector.GetType() == typeof (QuerySourceReferenceExpression))
            {
                expression = string.Concat(GetN1QlExpression(selectClause.Selector), ".*");
            }
            else if (selectClause.Selector.NodeType == ExpressionType.New)
            {
                expression = N1QlExpressionTreeVisitor.GetN1QlSelectNewExpression(selectClause.Selector as NewExpression,
                    _parameterAggregator, _methodCallTranslatorProvider);
            }
            else
            {
                expression = GetN1QlExpression(selectClause.Selector);
            }

            return expression;
        }

        public override void VisitWhereClause(WhereClause whereClause, QueryModel queryModel, int index)
        {
            _queryPartsAggregator.AddWherePart(GetN1QlExpression(whereClause.Predicate));
            base.VisitWhereClause(whereClause, queryModel, index);
        }

        public void VisitWhereMissingClause(WhereMissingClause whereClause, QueryModel queryModel, int index)
        {
            var expression = GetN1QlExpression(whereClause.Predicate);
            _queryPartsAggregator.AddWhereMissingPart(String.Concat(expression, " IS MISSING"));
        }

        public override void VisitResultOperator(ResultOperatorBase resultOperator, QueryModel queryModel, int index)
        {
            if ((resultOperator is TakeResultOperator))
            {
                var takeResultOperator = resultOperator as TakeResultOperator;

                _queryPartsAggregator.AddLimitPart(" LIMIT {0}",
                    Convert.ToInt32(GetN1QlExpression(takeResultOperator.Count)));
            }
            else if (resultOperator is SkipResultOperator)
            {
                var skipResultOperator = resultOperator as SkipResultOperator;

                _queryPartsAggregator.AddOffsetPart(" OFFSET {0}",
                    Convert.ToInt32(GetN1QlExpression(skipResultOperator.Count)));
            }
            else if (resultOperator is DistinctResultOperator)
            {
                var distinctResultOperator = resultOperator as DistinctResultOperator;
                _queryPartsAggregator.AddDistinctPart("DISTINCT ");
            }
            else if (resultOperator is ExplainResultOperator)
            {
                _queryPartsAggregator.ExplainPart = "EXPLAIN ";
            }
            else if (resultOperator is AnyResultOperator)
            {
                _queryPartsAggregator.QueryType = _isSubQuery ? N1QlQueryType.Any : N1QlQueryType.AnyMainQuery;
            }
            else if (resultOperator is AllResultOperator)
            {
                var allResultOperator = (AllResultOperator) resultOperator;
                _queryPartsAggregator.WhereAllPart = GetN1QlExpression(allResultOperator.Predicate);

                _queryPartsAggregator.QueryType = _isSubQuery ? N1QlQueryType.All : N1QlQueryType.AllMainQuery;
            }


            base.VisitResultOperator(resultOperator, queryModel, index);
        }

        public override void VisitOrderByClause(OrderByClause orderByClause, QueryModel queryModel, int index)
        {
            var orderByParts =
                orderByClause.Orderings.Select(
                    ordering =>
                        String.Concat(GetN1QlExpression(ordering.Expression), " ",
                            ordering.OrderingDirection.ToString().ToUpper())).ToList();

            _queryPartsAggregator.AddOrderByPart(orderByParts);

            base.VisitOrderByClause(orderByClause, queryModel, index);
        }

        #region Additional From Clauses

        public override void VisitAdditionalFromClause(AdditionalFromClause fromClause, QueryModel queryModel, int index)
        {
            var handled = false;

            switch (fromClause.FromExpression.NodeType)
            {
                case ExpressionType.MemberAccess:
                    // Unnest operation

                    var fromPart = VisitMemberFromExpression(fromClause, fromClause.FromExpression as MemberExpression);
                    _queryPartsAggregator.AddFromPart(fromPart);
                    handled = true;
                    break;

                case (ExpressionType)100002: // SubQueryExpression
                    // Might be an unnest or a join to another bucket

                    handled = VisitSubQueryFromExpression(fromClause, fromClause.FromExpression as SubQueryExpression);
                    break;
            }

            if (!handled)
            {
                throw new NotSupportedException("N1QL Does Not Support This Type Of From Clause");
            }

            base.VisitAdditionalFromClause(fromClause, queryModel, index);
        }

        /// <summary>
        /// Visits an AdditionalFromClause that is executing a subquery
        /// </summary>
        /// <param name="fromClause">AdditionalFromClause being visited</param>
        /// <param name="subQuery">Subquery being executed by the AdditionalFromClause</param>
        /// <returns>True if handled</returns>
        private bool VisitSubQueryFromExpression(AdditionalFromClause fromClause, SubQueryExpression subQuery)
        {
            var mainFromExpression = subQuery.QueryModel.MainFromClause.FromExpression;

            switch (mainFromExpression.NodeType)
            {
                case (ExpressionType)100001: // QuerySourceReferenceExpression
                    // Joining to another bucket using a previous group join operation

                    return VisitSubQuerySourceReferenceExpression(fromClause, subQuery, mainFromExpression as QuerySourceReferenceExpression);

                case ExpressionType.MemberAccess:
                    // Unnest operation

                    var fromPart = VisitMemberFromExpression(fromClause, mainFromExpression as MemberExpression);

                    if (subQuery.QueryModel.ResultOperators.OfType<DefaultIfEmptyResultOperator>().Any())
                    {
                        fromPart.JoinType = "OUTER UNNEST";
                    }

                    _queryPartsAggregator.AddFromPart(fromPart);

                    // be sure the subquery clauses use the provided itemName
                    subQuery.QueryModel.MainFromClause.ItemName = fromClause.ItemName;

                    // Apply where filters in the subquery to the main query
                    VisitBodyClauses(subQuery.QueryModel.BodyClauses, subQuery.QueryModel);

                    return true;
            }

            return false;
        }

        /// <summary>
        /// Visit an AdditionalFromClause referencing a previous group join clause
        /// </summary>
        /// <param name="fromClause">AdditionalFromClause being visited</param>
        /// <param name="subQuery">SubQueryExpression being visited</param>
        /// <param name="querySourceReference">QuerySourceReferenceExpression that is the MainFromClause of the SubQuery</param>
        /// <returns>N1QlFromQueryPart to be added to the QueryPartsAggregator.  JoinType is defaulted to INNER UNNEST.</returns>
        private bool VisitSubQuerySourceReferenceExpression(AdditionalFromClause fromClause, SubQueryExpression subQuery,
            QuerySourceReferenceExpression querySourceReference)
        {
            var unclaimedJoin =
                    _unclaimedGroupJoins.FirstOrDefault(
                        p => p.GroupJoinClause == querySourceReference.ReferencedQuerySource);
            if (unclaimedJoin != null)
            {
                // this additional from clause is for a previous group join
                // if not, then it isn't supported and we'll let the method return false so an exception is thrown

                var fromPart = ParseJoinClause(unclaimedJoin.JoinClause, fromClause.ItemName);

                if (subQuery.QueryModel.ResultOperators.OfType<DefaultIfEmptyResultOperator>().Any())
                {
                    fromPart.JoinType = "LEFT JOIN";

                    // TODO Handle where clauses applied to the inner sequence before the join
                    // Currently they are filtered after the join is complete instead of before by N1QL
                }

                _unclaimedGroupJoins.Remove(unclaimedJoin);
                _queryPartsAggregator.AddFromPart(fromPart);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Visit an AdditionalFromClause referencing a member
        /// </summary>
        /// <param name="fromClause">AdditionalFromClause being visited</param>
        /// <param name="expression">MemberExpression being referenced</param>
        /// <returns>N1QlFromQueryPart to be added to the QueryPartsAggregator.  JoinType is defaulted to INNER UNNEST.</returns>
        private N1QlFromQueryPart VisitMemberFromExpression(AdditionalFromClause fromClause, MemberExpression expression)
        {
            // This case represents an unnest operation

            return new N1QlFromQueryPart()
            {
                Source = GetN1QlExpression(expression),
                ItemName = EscapeIdentifier(fromClause.ItemName),
                JoinType = "INNER UNNEST"
            };
        }
        
        #endregion

        #region Join Clauses

        public override void VisitJoinClause(JoinClause joinClause, QueryModel queryModel,
            GroupJoinClause groupJoinClause)
        {
            // Store the group join with the expectation it will be used later by an additional from clause

            _unclaimedGroupJoins.Add(new UnclaimedGroupJoin()
            {
                JoinClause = joinClause,
                GroupJoinClause = groupJoinClause
            });

            base.VisitJoinClause(joinClause, queryModel, groupJoinClause);
        }

        public override void VisitJoinClause(JoinClause joinClause, QueryModel queryModel, int index)
        {
            // basic join clause is an INNER JOIN against another bucket

            var fromQueryPart = ParseJoinClause(joinClause, joinClause.ItemName);

            _queryPartsAggregator.AddFromPart(fromQueryPart);

            base.VisitJoinClause(joinClause, queryModel, index);
        }

        /// <summary>
        /// Visits a join against either a constant expression of IBucketQueryable, or a subquery based on an IBucketQueryable
        /// </summary>
        /// <param name="joinClause">Join clause being visited</param>
        /// <param name="itemName">Name to be used when referencing the data being joined</param>
        /// <returns>N1QlFromQueryPart to be added to the QueryPartsAggregator.  JoinType is defaulted to INNER JOIN.</returns>
        /// <remarks>The InnerKeySelector must be selecting the N1Ql.Key of the InnerSequence</remarks>
        private N1QlFromQueryPart ParseJoinClause(JoinClause joinClause, string itemName)
        {
            switch (joinClause.InnerSequence.NodeType)
            {
                case ExpressionType.Constant:
                    return VisitConstantExpressionJoinClause(joinClause, joinClause.InnerSequence as ConstantExpression, itemName);

                case (ExpressionType)100002: // SubQueryExpression
                    var subQuery = joinClause.InnerSequence as SubQueryExpression;
                    if ((subQuery == null) || subQuery.QueryModel.ResultOperators.Any() || subQuery.QueryModel.MainFromClause.FromExpression.NodeType != ExpressionType.Constant)
                    {
                        throw new NotSupportedException("Unsupported Join Inner Sequence");
                    }

                    // be sure the subquery clauses use the provided itemName
                    subQuery.QueryModel.MainFromClause.ItemName = itemName;

                    var fromPart = VisitConstantExpressionJoinClause(joinClause,
                        subQuery.QueryModel.MainFromClause.FromExpression as ConstantExpression, itemName);

                    VisitBodyClauses(subQuery.QueryModel.BodyClauses, subQuery.QueryModel);
                    
                    return fromPart;

                default:
                    throw new NotSupportedException("Unsupported Join Inner Sequence");
            }
        }

        /// <summary>
        /// Visits a join against a constant expression, which must be an IBucketQueryable implementation
        /// </summary>
        /// <param name="joinClause">Join clause being visited</param>
        /// <param name="constantExpression">Constant expression that is the InnerSequence of the JoinClause</param>
        /// <param name="itemName">Name to be used when referencing the data being joined</param>
        /// <returns>N1QlFromQueryPart to be added to the QueryPartsAggregator.  JoinType is defaulted to INNER JOIN.</returns>
        /// <remarks>The InnerKeySelector must be selecting the N1Ql.Key of the InnerSequence</remarks>
        private N1QlFromQueryPart VisitConstantExpressionJoinClause(JoinClause joinClause, ConstantExpression constantExpression, string itemName)
        {
            string bucketName = null;

            if (constantExpression != null)
            {
                var bucketQueryable = constantExpression.Value as IBucketQueryable;
                if (bucketQueryable != null)
                {
                    bucketName = bucketQueryable.BucketName;
                }
            }

            if (bucketName == null)
            {
                throw new NotSupportedException("N1QL Joins Must Be Against IBucketQueryable");
            }

            var keyExpression = joinClause.InnerKeySelector as MethodCallExpression;
            if ((keyExpression == null) ||
                (keyExpression.Method != typeof(N1Ql).GetMethod("Key")) ||
                (keyExpression.Arguments.Count != 1))
            {
                throw new NotSupportedException("N1QL Join Selector Must Be A Call To N1Ql.Key");
            }

            if (!(keyExpression.Arguments[0] is QuerySourceReferenceExpression))
            {
                throw new NotSupportedException("N1QL Join Selector Call To N1Ql.Key Must Reference The Inner Sequence");
            }

            return new N1QlFromQueryPart()
            {
                Source = EscapeIdentifier(bucketName),
                ItemName = EscapeIdentifier(itemName),
                OnKeys = GetN1QlExpression(joinClause.OuterKeySelector),
                JoinType = "INNER JOIN"
            };
        }

        #endregion

        private string GetN1QlExpression(Expression expression)
        {
            return N1QlExpressionTreeVisitor.GetN1QlExpression(expression, _parameterAggregator, _methodCallTranslatorProvider);
        }

        /// <summary>
        ///     Ensures that if the identifier contains a hyphen or other special characters that it will be escaped by tick (`) characters.
        /// </summary>
        /// <param name="identifier">The identifier to format</param>
        /// <returns>An escaped identifier, if escaping was required.  Otherwise the original identifier.</returns>
        public static string EscapeIdentifier(string identifier)
        {
            if (identifier == null)
            {
                throw new ArgumentNullException("identifier");
            }

            bool containsSpecialChar = false;
            for (var i = 0; i < identifier.Length; i++)
            {
                if (!Char.IsLetterOrDigit(identifier[i]))
                {
                    containsSpecialChar = true;
                    break;
                }
            }

            if (!containsSpecialChar)
            {
                return identifier;
            }
            else
            {
                var sb = new System.Text.StringBuilder(identifier.Length + 2);

                sb.Append('`');
                sb.Append(identifier.Replace("`", "``"));
                sb.Append('`');
                return sb.ToString();
            }
        }
    }
}