using System;
using System.Collections.Generic;
using System.Linq;
using CppSharp.AST;
using Type = CppSharp.AST.Type;

namespace CppSharp.Passes
{
    /// <summary>
    ///     Some kinds of fields are not supported in c#, in particular fixed arrays can only be of a primitive type.
    /// </summary>
    public class UnwrapUnsupportedArraysPass : TranslationUnitPass
    {
        public override bool VisitClassDecl(Class @class)
        {
            var toRemove = new List<Field>();
            var toAdd = new List<Tuple<int, Field>>();
            for (int index = 0; index < @class.Fields.Count; index++)
            {
                Field field = @class.Fields[index];
                int fieldIndex = index;
                List<Field> unwrapped = TryUnwrapField(@class, field).ToList();
                if (unwrapped.Any())
                    toRemove.Add(field);
                toAdd.AddRange(unwrapped.Select(v => Tuple.Create(fieldIndex, v)));
            }
            toAdd.Reverse();
            foreach (var field in toAdd)
            {
                @class.Fields.Insert(field.Item1, field.Item2);
            }

            foreach (Field field in toRemove)
            {
                @class.Fields.Remove(field);
            }
            return true;
        }

        private IEnumerable<Field> TryUnwrapField(Class @class, Field field)
        {
            // check if field is an array
            var arrayType = field.Type as ArrayType;
            if (arrayType != null && arrayType.SizeType == ArrayType.ArraySize.Constant)
            {
                Type typeInArray = arrayType.Type.Desugar();
                long unwrapCount = 0;
                Type unwrapType = null;
                // in bits
                long unwrapTypeWidth = 0;

                if (typeInArray.IsPrimitiveType())
                {
                    // no need to unwrap, as we can handle generate fixed arrays of primitive types
                    yield break;
                }

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
                    unwrapCount = arrayType.Size / ((ArrayType)(typeInArray)).Size;
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
                    Driver.Diagnostics.EmitWarning("Failed to unwrap {0}.{1} ({2})",
                        @class.Name, field.Name, field.Type);
                    yield break;
                }

                Driver.Diagnostics.EmitMessage("Unwrapping {0}.{1} ({2})",
                    @class.Name, field.Name, field.Type);

                for (int i = 0; i < unwrapCount; i++)
                {
                    string unwrappedName = field.Name + "_" + i;

                    var unwrappedField = new Field
                    {
                        Name = unwrappedName,
                        Offset = (uint) (field.Offset + i*unwrapTypeWidth),
                        Class = @class,
                        DebugText = field.DebugText,
                        DefinitionOrder = field.DefinitionOrder,
                        Namespace = field.Namespace,
                        QualifiedType = new QualifiedType(unwrapType, field.QualifiedType.Qualifiers),
                        IgnoreFlags = field.IgnoreFlags,
                        IsGenerated = field.IsGenerated,
                        ExcludeFromPasses = field.ExcludeFromPasses,
                        ExplicityIgnored = field.ExplicityIgnored,
                        CompleteDeclaration = field.CompleteDeclaration,
                        Comment = field.Comment,
                        IsIncomplete = field.IsIncomplete,
                        Access = field.Access,
                        IsDependent = field.IsDependent,
                    };

                    IEnumerable<Field> unwrappedAgain = TryUnwrapField(@class, unwrappedField).ToList();
                    if (unwrappedAgain.Any())
                    {
                        foreach (Field field1 in unwrappedAgain)
                        {
                            yield return field1;
                        }
                    }
                    else
                    {
                        yield return unwrappedField;
                    }
                }
            }
        }
    }
}