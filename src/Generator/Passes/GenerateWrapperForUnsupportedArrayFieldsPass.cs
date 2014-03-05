using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CppSharp.AST;
using Type = CppSharp.AST.Type;

namespace CppSharp.Passes
{
    /// <summary>
    ///     Some kinds of fields are not supported in C# pinvoke, in particular fixed arrays can only be of a primitive type.
    /// </summary>
    public class GenerateWrapperForUnsupportedArrayFieldsPass : TranslationUnitPass
    {
        public override bool VisitClassDecl(Class @class)
        {
            ProcessClass(@class, @class.Namespace);
            return true;
        }

        private void ProcessClass(Class @class, DeclarationContext ctx)
        {
            foreach (Field field in @class.Fields)
            {
                ArrayType arrayType;
                Type typeInArray;
                var oldType = field.Type;
                if (!ShouldUnwrap(oldType, out arrayType, out typeInArray))
                {
                    continue;
                }

                UnwrapInfo? unwrapInfo = GetUnwrapInfo(arrayType, typeInArray);
                // we should not get null, since the ShouldUnwrap check above should have 
                if (unwrapInfo == null)
                {
                    Driver.Diagnostics.EmitWarning("Failed to handle const-size array field {0}.{1} of type {2}",
                        @class.Name, field.Name, oldType);
                    continue;
                }

                // generate wrapper class types in class namespace (for reuse)
                var wrapperClass = GetCreateWrapperStruct(ctx, field, unwrapInfo.Value);

                field.QualifiedType = new QualifiedType(new TagType(wrapperClass));

                Driver.Diagnostics.EmitMessage("Modified Field {0}.{1} from type {2} to type {3}",
                    @class.Name, field.Name, oldType, field.Type);
            }

            // may have to update indexer in class 
            // (in the case where we are unwrapping an arraywrapper class, relevant for multi-dimensional arrays)
            var arrayWrapperClass = @class as ArrayWrapperClass;
            if(arrayWrapperClass != null)
            {
                if (arrayWrapperClass.Indexer != null)
                {
                    arrayWrapperClass.Indexer.QualifiedType = arrayWrapperClass.Fields[0].QualifiedType;
                }
            }
        }

        private static bool ShouldUnwrap(Type type, out ArrayType arrayType, out Type typeInArray)
        {
            // check that field is a constant size array
            if (IsConstSizeArray(type, out arrayType))
            {
                typeInArray = arrayType.Type.Desugar();
                if (typeInArray.IsPrimitiveType())
                {
                    // no need to unwrap, as it is legal to have fixed arrays of primitive types (in C# pinvoke)
                    return false;
                }
                return true;
            }
            typeInArray = null;
            return false;
        }


        private static bool IsConstSizeArray(Type type, out ArrayType arrayType)
        {
            arrayType = type as ArrayType;
            if (arrayType == null || arrayType.SizeType != ArrayType.ArraySize.Constant)
            {
                return false;
            }
            return true;
        }

        private ArrayWrapperClass GetCreateWrapperStruct(DeclarationContext ctx, Field field, UnwrapInfo unwrapInfo)
        {
            string wrapperClassName = "ArrayWrapper_" + unwrapInfo.UnwrapType + unwrapInfo.UnwrapCount;

            var wrapperClass = ctx.FindClass(wrapperClassName) as ArrayWrapperClass;
            if (wrapperClass == null)
            {
                Driver.Diagnostics.EmitMessage("Creating new Array Wrapper Class {0}",
                    wrapperClassName);

                wrapperClass = new ArrayWrapperClass
                {
                    ArrayWrapperType = unwrapInfo.UnwrapType,
                    Namespace = ctx,
                    Name = wrapperClassName,
                    Type = ClassType.ValueType,
                };

                // Create length const field
                var lengthField = new Field
                {
                    Name = "Length",
                    Expression =
                        PrimitiveTypeExpression.TryCreate(unwrapInfo.UnwrapCount.ToString(CultureInfo.InvariantCulture)),
                    Access = AccessSpecifier.Public,
                    QualifiedType =
                        new QualifiedType(
                            new BuiltinType(
                                PrimitiveTypeExpression.TryCreate(
                                    unwrapInfo.UnwrapCount.ToString(CultureInfo.InvariantCulture)).Type),
                            new TypeQualifiers {IsConst = true})
                };

                // Add indexer (if we can..)
                ArrayIndexerProperty indexerProperty = null;
                bool unwrapTypeIsConstArray = unwrapInfo.UnwrapType is ArrayType && ((ArrayType)(unwrapInfo.UnwrapType)).SizeType == ArrayType.ArraySize.Constant;
                bool unwrapTypeIsPointer = unwrapInfo.UnwrapType is PointerType;
                if (!unwrapTypeIsConstArray)
                {
                    indexerProperty = new ArrayIndexerProperty
                    {
                        QualifiedType = new QualifiedType(unwrapInfo.UnwrapType),
                        Namespace = wrapperClass
                    };
                    indexerProperty.Parameters.Add(new Parameter
                    {
                        QualifiedType = lengthField.QualifiedType,
                        Name = "idx"
                    });

                    wrapperClass.Indexer = indexerProperty;
                    wrapperClass.Properties.Add(indexerProperty);
                }

                // generate "unwrapped" fields
                for (int i = 0; i < unwrapInfo.UnwrapCount; i++)
                {
                    // field[N] becomes field_0 .. field_N
                    string unwrappedName = "I" + i;

                    var unwrappedField = new Field
                    {
                        Name = unwrappedName,
                        Namespace = wrapperClass,
                        Offset = (uint) (i*unwrapInfo.UnwrapTypeWidth),
                        QualifiedType = new QualifiedType(unwrapInfo.UnwrapType, field.QualifiedType.Qualifiers),
                    };

                    if (indexerProperty != null)
                    {
                        indexerProperty.IndexToArray[i] = unwrappedField;
                    }

                    wrapperClass.Fields.Add(unwrappedField);
                }

                wrapperClass.Fields.Add(lengthField);

                ctx.Classes.Add(wrapperClass);
            }

            // we may have to unwrap the wrapper class too;P
            ProcessClass(wrapperClass, ctx);

            return wrapperClass;
        }

        private UnwrapInfo? GetUnwrapInfo(ArrayType arrayType, Type typeInArray)
        {
            long unwrapCount;
            Type unwrapType;
            // in bits
            long unwrapTypeWidth;
            if (typeInArray.IsPointerToPrimitiveType())
            {
                // need to unwrap if field is array of pointer to primitive type
                // int* A[N] 
                // becomes
                // int* A_0;
                // int* A_N;
                // unwrap to multiple pointer fields
                unwrapType = new PointerType
                {
                    QualifiedPointee = ((PointerType) typeInArray).QualifiedPointee
                };
                unwrapCount = arrayType.Size;
                // TODO: get type width from driver TargetInfo!
                unwrapTypeWidth = 0;
            }
            else if (typeInArray.IsPointerToArrayType())
            {
                // need to unwrap if field is array of pointer to array
                // A (*int[N])[M]
                // becomes
                // int** X_0;
                // int** X_M;
                var innerArray = (ArrayType) ((PointerType) typeInArray).Pointee;

                unwrapType = new PointerType
                {
                    QualifiedPointee = new QualifiedType(new PointerType
                    {
                        QualifiedPointee = new QualifiedType(innerArray.Type)
                    })
                };
                unwrapCount = arrayType.Size;
                unwrapTypeWidth = Driver.TargetInfo.PointerWidth;
            }
            else if (typeInArray is ArrayType)
            {
                // need to unwrap if field is array of array
                // int A[N][M]
                // becomes
                // int A_0[M];
                // int A_N[M];
                Type innerArray = ((ArrayType) (typeInArray)).Type;
                unwrapType = new ArrayType
                {
                    Size = ((ArrayType) (typeInArray)).Size,
                    SizeType = ArrayType.ArraySize.Constant,
                    Type = innerArray
                };
                unwrapCount = arrayType.Size/((ArrayType) (typeInArray)).Size;
                // TODO: get type width from driver TargetInfo!
                unwrapTypeWidth = 0;
            }
            else if (!typeInArray.IsPrimitiveType())
            {
                // need tp unwrap if field is array of complex type
                // Struct A[N]
                // becomes
                // Struct A_0;
                // Struct A_N;
                unwrapType = typeInArray;
                unwrapCount = arrayType.Size;
                // TODO: get type width from driver TargetInfo!
                unwrapTypeWidth = 0;
            }
            else
            {
                return null;
            }

            return new UnwrapInfo
            {
                UnwrapType = unwrapType,
                UnwrapCount = unwrapCount,
                UnwrapTypeWidth = unwrapTypeWidth
            };
        }

        private struct UnwrapInfo
        {
            public Type UnwrapType { get; set; }
            // in bits!
            public long UnwrapTypeWidth { get; set; }
            public long UnwrapCount { get; set; }
        }
    }
}