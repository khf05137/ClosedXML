using ClosedXML.Excel.CalcEngine.Exceptions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ClosedXML.Excel.CalcEngine
{
    /// <summary>
    /// Base class for all AST nodes. All AST nodes must be immutable.
    /// </summary>
    internal abstract class AstNode
    {
        /// <summary>
        /// Method to accept a vistor (=call a method of visitor with correct type of the node).
        /// </summary>
        public abstract TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor);
    }

    /// <summary>
    /// A base class for all AST nodes that can be evaluated to produce a value.
    /// </summary>
    internal abstract class Expression : AstNode, IComparable<Expression>
    {
        public abstract object Evaluate();

        //---------------------------------------------------------------------------

        #region ** implicit converters

        public static implicit operator string(Expression x)
        {
            if (x is ErrorExpression)
                (x as ErrorExpression).ThrowApplicableException();

            var v = x.Evaluate();

            if (v == null)
                return string.Empty;

            if (v is bool b)
                return b.ToString().ToUpper();

            return v.ToString();
        }

        public static implicit operator double(Expression x)
        {
            if (x is ErrorExpression)
                (x as ErrorExpression).ThrowApplicableException();

            // evaluate
            var v = x.Evaluate();

            // handle doubles
            if (v is double dbl)
            {
                return dbl;
            }

            // handle booleans
            if (v is bool b)
            {
                return b ? 1 : 0;
            }

            // handle dates
            if (v is DateTime dt)
            {
                return dt.ToOADate();
            }

            if (v is TimeSpan ts)
            {
                return ts.TotalDays;
            }

            // handle string
            if (v is string s && double.TryParse(s, out var doubleValue))
            {
                return doubleValue;
            }

            // handle nulls
            if (v == null || v is string)
            {
                return 0;
            }

            // handle everything else
            CultureInfo _ci = Thread.CurrentThread.CurrentCulture;
            return (double)Convert.ChangeType(v, typeof(double), _ci);
        }

        public static implicit operator bool(Expression x)
        {
            if (x is ErrorExpression)
                (x as ErrorExpression).ThrowApplicableException();

            // evaluate
            var v = x.Evaluate();

            // handle booleans
            if (v is bool b)
            {
                return b;
            }

            // handle nulls
            if (v == null)
            {
                return false;
            }

            // handle doubles
            if (v is double dbl)
            {
                return dbl != 0;
            }

            // handle everything else
            return (double)Convert.ChangeType(v, typeof(double)) != 0;
        }

        public static implicit operator DateTime(Expression x)
        {
            if (x is ErrorExpression)
                (x as ErrorExpression).ThrowApplicableException();

            // evaluate
            var v = x.Evaluate();

            // handle dates
            if (v is DateTime dt)
            {
                return dt;
            }

            if (v is TimeSpan ts)
            {
                return new DateTime().Add(ts);
            }

            // handle numbers
            if (v.IsNumber())
            {
                return DateTime.FromOADate((double)x);
            }

            // handle everything else
            CultureInfo _ci = Thread.CurrentThread.CurrentCulture;
            return (DateTime)Convert.ChangeType(v, typeof(DateTime), _ci);
        }

        #endregion ** implicit converters

        //---------------------------------------------------------------------------

        #region ** IComparable<Expression>

        public int CompareTo(Expression other)
        {
            // get both values
            var c1 = this.Evaluate() as IComparable;
            var c2 = other.Evaluate() as IComparable;

            // handle nulls
            if (c1 == null && c2 == null)
            {
                return 0;
            }
            if (c2 == null)
            {
                return -1;
            }
            if (c1 == null)
            {
                return +1;
            }

            // make sure types are the same
            if (c1.GetType() != c2.GetType())
            {
                try
                {
                    if (c1 is DateTime)
                        c2 = ((DateTime)other);
                    else if (c2 is DateTime)
                        c1 = ((DateTime)this);
                    else
                        c2 = Convert.ChangeType(c2, c1.GetType()) as IComparable;
                }
                catch (InvalidCastException) { return -1; }
                catch (FormatException) { return -1; }
                catch (OverflowException) { return -1; }
                catch (ArgumentNullException) { return -1; }
            }

            // String comparisons should be case insensitive
            if (c1 is string s1 && c2 is string s2)
                return StringComparer.OrdinalIgnoreCase.Compare(s1, s2);
            else
                return c1.CompareTo(c2);
        }

        #endregion ** IComparable<Expression>
    }

    /// <summary>
    /// AST node that contains a number, text or a bool.
    /// </summary>
    internal class ScalarNode : Expression
    {
        private readonly object _value;

        public ScalarNode(object value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public override object Evaluate()
        {
            return _value;
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    internal enum UnaryOp
    {
        Add,
        Subtract,
        Percentage,
        SpillRange,
        ImplicitIntersection
    }

    /// <summary>
    /// Unary expression, e.g. +123
    /// </summary>
    internal class UnaryExpression : Expression
    {
        public UnaryExpression(UnaryOp operation, Expression expr)
        {
            Operation = operation;
            Expression = expr;
        }

        public UnaryOp Operation { get; }

        public Expression Expression { get; private set; }

        // ** object model
        override public object Evaluate()
        {
            switch (Operation)
            {
                case UnaryOp.Add:
                    return Expression.Evaluate();

                case UnaryOp.Subtract:
                    return -(double)Expression;

                case UnaryOp.Percentage:
                    return ((double)Expression) / 100.0;

                case UnaryOp.SpillRange:
                    throw new NotImplementedException("Evaluation of spill range operator is not implemented.");

                case UnaryOp.ImplicitIntersection:
                    throw new NotImplementedException("Evaluation of implicit intersection operator is not implemented.");
            }
            throw new ArgumentException("Bad expression.");
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    internal enum BinaryOp
    {
        // Text operators
        Concat,
        // Arithmetic
        Add,
        Sub,
        Mult,
        Div,
        Exp,
        // Comparison operators
        Lt,
        Lte,
        Eq,
        Neq,
        Gte,
        Gt,
        // References operators
        Range,
        Union,
        Intersection
    }

    /// <summary>
    /// Binary expression, e.g. 1+2
    /// </summary>
    internal class BinaryExpression : Expression
    {
        private static readonly HashSet<BinaryOp> _comparisons = new HashSet<BinaryOp>
        {
            BinaryOp.Lt,
            BinaryOp.Lte,
            BinaryOp.Eq,
            BinaryOp.Neq,
            BinaryOp.Gte,
            BinaryOp.Gt
        };

        private readonly bool _isComparison;

        public BinaryExpression(BinaryOp operation, Expression exprLeft, Expression exprRight)
        {
            _isComparison = _comparisons.Contains(operation);
            Operation = operation;
            LeftExpression = exprLeft;
            RightExpression = exprRight;
        }

        public BinaryOp Operation { get; }

        public Expression LeftExpression { get; private set; }
        public Expression RightExpression { get; private set; }

        // ** object model
        override public object Evaluate()
        {
            // handle comparisons
            if (_isComparison)
            {
                var cmp = LeftExpression.CompareTo(RightExpression);
                switch (Operation)
                {
                    case BinaryOp.Gt: return cmp > 0;
                    case BinaryOp.Lt: return cmp < 0;
                    case BinaryOp.Gte: return cmp >= 0;
                    case BinaryOp.Lte: return cmp <= 0;
                    case BinaryOp.Eq: return cmp == 0;
                    case BinaryOp.Neq: return cmp != 0;
                }
            }

            // handle everything else
            switch (Operation)
            {
                case BinaryOp.Concat:
                    return (string)LeftExpression + (string)RightExpression;

                case BinaryOp.Add:
                    return (double)LeftExpression + (double)RightExpression;

                case BinaryOp.Sub:
                    return (double)LeftExpression - (double)RightExpression;

                case BinaryOp.Mult:
                    return (double)LeftExpression * (double)RightExpression;

                case BinaryOp.Div:
                    if (Math.Abs((double)RightExpression) < double.Epsilon)
                        throw new DivisionByZeroException();

                    return (double)LeftExpression / (double)RightExpression;

                case BinaryOp.Exp:
                    var a = (double)LeftExpression;
                    var b = (double)RightExpression;
                    if (b == 0.0) return 1.0;
                    if (b == 0.5) return Math.Sqrt(a);
                    if (b == 1.0) return a;
                    if (b == 2.0) return a * a;
                    if (b == 3.0) return a * a * a;
                    if (b == 4.0) return a * a * a * a;
                    return Math.Pow((double)LeftExpression, (double)RightExpression);
                case BinaryOp.Range:
                    throw new NotImplementedException("Evaluation of binary range operator is not implemented.");
                case BinaryOp.Union:
                    throw new NotImplementedException("Evaluation of range union operator is not implemented.");
                case BinaryOp.Intersection:
                    throw new NotImplementedException("Evaluation of range intersection operator is not implemented.");
            }

            throw new ArgumentException("Bad expression.");
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// Function call expression, e.g. sin(0.5)
    /// </summary>
    internal class FunctionExpression : Expression
    {
        public FunctionExpression(FunctionDefinition function, List<Expression> parms) : this(null, function, parms)
        { }

        public FunctionExpression(PrefixNode prefix, FunctionDefinition function, List<Expression> parms)
        {
            Prefix = prefix;
            FunctionDefinition = function;
            Parameters = parms;
        }

        // ** object model
        override public object Evaluate()
        {
            return FunctionDefinition.Function(Parameters);
        }

        public PrefixNode Prefix { get; }

        public FunctionDefinition FunctionDefinition { get; }

        public List<Expression> Parameters { get; }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// Expression that represents an external object.
    /// </summary>
    internal class XObjectExpression : Expression, IEnumerable
    {
        private readonly object _value;

        // ** ctor
        internal XObjectExpression(object value)
        {
            _value = value;
        }

        public object Value { get { return _value; } }

        // ** object model
        public override object Evaluate()
        {
            // use IValueObject if available
            var iv = _value as IValueObject;
            if (iv != null)
            {
                return iv.GetValue();
            }

            // return raw object
            return _value;
        }

        public IEnumerator GetEnumerator()
        {
            if (_value is string s)
            {
                yield return s;
            }
            else if (_value is IEnumerable ie)
            {
                foreach (var o in ie)
                    yield return o;
            }
            else
            {
                yield return _value;
            }
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// Expression that represents an omitted parameter.
    /// </summary>
    internal class EmptyValueExpression : Expression
    {
        public override object Evaluate() => null;

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    internal class ErrorExpression : Expression
    {
        internal enum ExpressionErrorType
        {
            CellReference,
            CellValue,
            DivisionByZero,
            NameNotRecognized,
            NoValueAvailable,
            NullValue,
            NumberInvalid
        }

        private readonly ExpressionErrorType _errorType;

        internal ErrorExpression(ExpressionErrorType errorType)
        {
            _errorType = errorType;
        }

        public override object Evaluate()
        {
            return _errorType;
        }

        public void ThrowApplicableException()
        {
            switch (_errorType)
            {
                // TODO: include last token in exception message
                case ExpressionErrorType.CellReference:
                    throw new CellReferenceException();
                case ExpressionErrorType.CellValue:
                    throw new CellValueException();
                case ExpressionErrorType.DivisionByZero:
                    throw new DivisionByZeroException();
                case ExpressionErrorType.NameNotRecognized:
                    throw new NameNotRecognizedException();
                case ExpressionErrorType.NoValueAvailable:
                    throw new NoValueAvailableException();
                case ExpressionErrorType.NullValue:
                    throw new NullValueException();
                case ExpressionErrorType.NumberInvalid:
                    throw new NumberException();
            }
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// An placeholder node for AST nodes that are not yet supported in ClosedXML.
    /// </summary>
    internal class NotSupportedNode : Expression
    {
        private readonly string _featureText;

        public NotSupportedNode(string featureText)
        {
            _featureText = featureText;
        }

        public override object Evaluate()
        {
            throw new NotImplementedException($"Evaluation of {_featureText} is not implemented.");
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// AST node for an reference to an external file in a formula.
    /// </summary>
    internal class FileNode : AstNode
    {
        /// <summary>
        /// If the file is references indirectly, numeric identifier of a file.
        /// </summary>
        public int? Numeric { get; }

        /// <summary>
        /// If a file is referenced directly, a path to the file on the disc/UNC/web link, .
        /// </summary>
        public string Path { get; }

        public FileNode(string path)
        {
            Path = path;
        }

        public FileNode(int numeric)
        {
            Numeric = numeric;
        }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// AST node for prefix of a reference in a formula. Prefix is a specification where to look for a reference.
    /// <list type="bullet">
    /// <item>Prefix specifies a <c>Sheet</c> - used for references in the local workbook.</item>
    /// <item>Prefix specifies a <c>FirstSheet</c> and a <c>LastSheet</c> - 3D reference, references uses all sheets between first and last.</item>
    /// <item>Prefix specifies a <c>File</c>, no sheet is specified - used for named ranges in external file.</item>
    /// <item>Prefix specifies a <c>File</c> and a <c>Sheet</c> - references looks for its address in the sheet of the file.</item>
    /// </list>
    /// </summary>
    internal class PrefixNode : AstNode
    {
        public PrefixNode(FileNode file, string sheet, string firstSheet, string lastSheet)
        {
            File = file;
            Sheet = sheet;
            FirstSheet = firstSheet;
            LastSheet = lastSheet;
        }

        /// <summary>
        /// If prefix references data from another file, can be empty.
        /// </summary>
        public FileNode File { get; }

        /// <summary>
        /// Name of the sheet, without ! or escaped quotes. Can be empty in some cases (e.g. reference to a named range in an another file).
        /// </summary>
        public string Sheet { get; }

        /// <summary>
        /// If the prefix is for 3D reference, name of first sheet. Empty otherwise.
        /// </summary>
        public string FirstSheet { get; }

        /// <summary>
        /// If the prefix is for 3D reference, name of the last sheet. Empty otherwise.
        /// </summary>
        public string LastSheet { get; }

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// AST node for a reference of an area in some sheet.
    /// </summary>
    internal class ReferenceNode : Expression
    {
        public ReferenceNode(PrefixNode prefix, ReferenceItemType type, string address)
        {
            Prefix = prefix;
            Type = type;
            Address = address;
        }

        /// <summary>
        /// An optional prefix for reference item.
        /// </summary>
        public PrefixNode Prefix { get; }

        public ReferenceItemType Type { get; }

        /// <summary>
        /// An address of a reference that corresponds to <see cref="Type"/>.
        /// </summary>
        public string Address { get; }

        public override object Evaluate() => throw new NotImplementedException("Evaluation of reference is not implemented.");

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    internal enum ReferenceItemType { Cell, NamedRange, VRange, HRange }

    // TODO: The AST node doesn't have any stuff from StructuredReference term because structured reference is not yet suported and
    // the SR grammar has changed in not-yet-released (after 1.5.2) version of XLParser
    internal class StructuredReferenceNode : Expression
    {
        public StructuredReferenceNode(PrefixNode prefix)
        {
            Prefix = prefix;
        }

        /// <summary>
        /// Can be empty if no prefix available.
        /// </summary>
        public PrefixNode Prefix { get; }

        public override object Evaluate() => throw new NotImplementedException("Evaluation of structured references is not implemented.");

        public override TResult Accept<TContext, TResult>(TContext context, IFormulaVisitor<TContext, TResult> visitor) => visitor.Visit(context, this);
    }

    /// <summary>
    /// Interface supported by external objects that have to return a value
    /// other than themselves (e.g. a cell range object should return the
    /// cell content instead of the range itself).
    /// </summary>
    public interface IValueObject
    {
        object GetValue();
    }
}
