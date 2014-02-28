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
            for (int index = 0; index < @class.Fields.Count; index++)
            {
                Field field = @class.Fields[index];

                Class wrapperClass = ProcessField(@class, field, ctx);

                if (wrapperClass != null)
                {
                    var wrapperField = new Field(field)
                    {
                        QualifiedType = new QualifiedType(new TagType(wrapperClass)),
                    };

                    @class.Fields[index] = wrapperField;
                }
            }
        }

        private Class ProcessField(Class @class, Field field, DeclarationContext ctx)
        {
            ArrayType arrayType;
            Type typeInArray;
            if (!ShouldUnwrap(field.Type, out arrayType, out typeInArray))
            {
                return null;
            }

            UnwrapInfo? unwrapInfo = GetUnwrapInfo(arrayType, typeInArray);
            if (unwrapInfo != null)
            {
                // generate wrapper class types in class namespace (for reuse)
                var wrapperClass = GetCreateWrapperStruct(ctx, field, unwrapInfo.Value);
                Driver.Diagnostics.EmitMessage("Wrapping {0}.{1} ({2}) as new Class {3} ({4})",
                    @class.Name, field.Name, field.Type, wrapperClass.Name, wrapperClass.Type);
                ProcessClass(wrapperClass, ctx);
                return wrapperClass;
            }

            Driver.Diagnostics.EmitWarning("Failed to unwrap {0}.{1} ({2})",
                @class.Name, field.Name, field.Type);
            return null;
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

        private Class GetCreateWrapperStruct(DeclarationContext ctx, Field field, UnwrapInfo unwrapInfo)
        {
            string wrapperClassName = "ArrayWrapper_" + unwrapInfo.UnwrapType + unwrapInfo.UnwrapCount;

            Class wrapperClass = ctx.FindClass(wrapperClassName);
            if (wrapperClass == null)
            {
                wrapperClass = new Class
                {
                    Namespace = ctx,
                    Name = wrapperClassName,
                    Type = ClassType.ValueType,
                };

                // cant have field with value in value type/struct... make property instead
//                PrimitiveTypeExpression primitiveTypeExpression =
//                    PrimitiveTypeExpression.TryCreate(unwrapInfo.UnwrapCount.ToString(CultureInfo.InvariantCulture));
//                wrapperClass.Fields.Add(new Field
//                {
//                    Name = "Length",
//                    Expression = primitiveTypeExpression,
//                    Access = AccessSpecifier.Public,
//                    QualifiedType = new QualifiedType(new BuiltinType(primitiveTypeExpression.Type))
//                });

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

                    wrapperClass.Fields.Add(unwrappedField);
                }

                ctx.Classes.Add(wrapperClass);
            }

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