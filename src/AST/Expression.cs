using System;
using System.Globalization;

namespace CppSharp.AST
{
    public abstract class Expression
    {
        public string DebugText;

        public abstract TV Visit<TV>(IExpressionVisitor<TV> visitor);
    }

    public class PrimitiveTypeExpression : Expression
    {
        private object _parsedValue;

        public PrimitiveTypeExpression(double value)
        {
            _parsedValue = value;
            Type = PrimitiveType.Double;
        }

        public PrimitiveTypeExpression(long value)
        {
            PrimitiveType type;
            ConvertLongToSmallestIntegerType(out type, out _parsedValue, value);
            Type = type;
        }

        private PrimitiveTypeExpression(string debugText, object parsedValue)
        {
            _parsedValue = parsedValue;
            DebugText = debugText;
        }

        public static PrimitiveTypeExpression TryCreate(string expression)
        {
            PrimitiveType type;
            object parsedValue;
            if(TryParseExpression(expression, out type, out parsedValue))
            {
                return new PrimitiveTypeExpression(expression, parsedValue)
                {
                    Type = type
                };
            }
            return null;
        }

        public PrimitiveType Type { get; private set; }

        private static bool TryParseExpression(string expression, out PrimitiveType primitiveType, out object parsedValue)
        {
            long valueAsInteger;
            if (ParseAsLong(expression, out valueAsInteger))
            {
                ConvertLongToSmallestIntegerType(out primitiveType, out parsedValue, valueAsInteger);
                return true;
            }
            double parsedDouble;
            if (ParseAsDouble(expression, out parsedDouble))
            {
                primitiveType = PrimitiveType.Double;
                parsedValue = parsedDouble;
                return true;
            }
            string parsedString;
            if (ParseAsString(expression, out parsedString))
            {
                primitiveType = PrimitiveType.WideChar;
                parsedValue = parsedString;
                return true;
            }
            primitiveType = PrimitiveType.Void;
            parsedValue = null;
            return false;
        }

        private static void ConvertLongToSmallestIntegerType(out PrimitiveType primitiveType, out object parsedValue,
            long valueAsInteger)
        {
            parsedValue = valueAsInteger;
            if (valueAsInteger <= System.SByte.MaxValue && valueAsInteger > System.SByte.MinValue)
            {
                primitiveType = PrimitiveType.Int8;
            }
            else if (valueAsInteger <= System.Byte.MaxValue && valueAsInteger > System.Byte.MinValue)
            {
                primitiveType = PrimitiveType.UInt8;
            }
            else if (valueAsInteger <= System.Int16.MaxValue && valueAsInteger > System.Int16.MinValue)
            {
                primitiveType = PrimitiveType.Int16;
            }
            else if (valueAsInteger <= System.UInt16.MaxValue && valueAsInteger > System.UInt16.MinValue)
            {
                primitiveType = PrimitiveType.UInt16;
            }
            else if (valueAsInteger <= System.Int32.MaxValue && valueAsInteger > System.Int32.MinValue)
            {
                primitiveType = PrimitiveType.Int32;
            }
            else if (valueAsInteger <= System.UInt32.MaxValue && valueAsInteger > System.UInt32.MinValue)
            {
                primitiveType = PrimitiveType.UInt32;
            }
            else if (valueAsInteger <= System.Int64.MaxValue && valueAsInteger > System.Int64.MinValue)
            {
                primitiveType = PrimitiveType.Int64;
            }
//                else if (valueAsInteger <= System.UInt64.MaxValue && valueAsInteger < System.UInt64.MinValue)
//                {
//                    primitiveType = PrimitiveType.UInt64;
//                }
            else
            {
                primitiveType = PrimitiveType.Void;
            }
        }

        private static bool ParseAsDouble(string expression, out double parsedDouble)
        {
            return double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble);
        }

        private static bool ParseAsString(string expression, out string valueAsString)
        {
            if (expression.StartsWith("\"") && expression.EndsWith("\""))
            {
                valueAsString = expression.TrimStart('"').TrimEnd('"');
                return true;
            }
            valueAsString = null;
            return false;
        }

        private static bool ParseAsLong(string num, out long val)
        {
            if (num.StartsWith("0x", StringComparison.CurrentCultureIgnoreCase))
            {
                num = num.Substring(2);

                return long.TryParse(num, NumberStyles.HexNumber,
                    CultureInfo.CurrentCulture, out val);
            }

            return long.TryParse(num, out val);
        }

        private static bool IsHexadecimal(string val)
        {
            if (val == null)
            {
                return false;
            }
            return val.Contains("0x") || val.Contains("0X");
        }

        public override string ToString()
        {
            switch (Type)
            {
                case PrimitiveType.WideChar:
                    return '"' + (string) _parsedValue + '"';
                case PrimitiveType.Double:
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0}D", ((double) _parsedValue));
                }
                case PrimitiveType.Int8:
                case PrimitiveType.UInt8:
                case PrimitiveType.Int16:
                case PrimitiveType.UInt16:
                case PrimitiveType.Int32:
                case PrimitiveType.UInt32:
                case PrimitiveType.Int64:
                case PrimitiveType.UInt64:
                {
                    bool printAsHex = IsHexadecimal(DebugText);
                    string format = printAsHex ? "x" : string.Empty;
                    return ((long) _parsedValue).ToString(format);
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override T Visit<T>(IExpressionVisitor<T> visitor)
        {
            return visitor.VisitBuiltinExpression(this);
        }
    }

    public interface IExpressionVisitor<out T>
    {
        T VisitBuiltinExpression(PrimitiveTypeExpression primitiveType);
    }
}