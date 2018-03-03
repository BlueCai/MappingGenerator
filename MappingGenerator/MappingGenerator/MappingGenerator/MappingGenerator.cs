using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Editing;

namespace MappingGenerator
{
    public class MappingGenerator
    {
        private readonly SyntaxGenerator generator;
        private readonly SemanticModel semanticModel;
        private static string[] SimpleTypes = new[] {"String", "Decimal"};
        private static char[] FobiddenSigns = new[] {'.', '[', ']', '(', ')'};

        public MappingGenerator(SyntaxGenerator generator, SemanticModel semanticModel)
        {
            this.generator = generator;
            this.semanticModel = semanticModel;
        }

        public IEnumerable<SyntaxNode> MapTypes(ITypeSymbol sourceType, ITypeSymbol targetType,
            SyntaxNode globalSourceAccessor, SyntaxNode globbalTargetAccessor = null, bool targetExists = false,
            bool generatorContext = false)
        {
            if (IsMappingBetweenCollections(targetType, sourceType))
            {
                var collectionMapping = MapCollections(globalSourceAccessor, sourceType, targetType);
                if (globbalTargetAccessor == null)
                {
                    yield return generator.ContextualReturnStatement(collectionMapping, generatorContext);    
                }
                else if(targetExists == false)
                {
                    yield return generator.CompleteAssignmentStatement(globbalTargetAccessor, collectionMapping);
                }
                yield break;
            }
            
            var targetLocalVariableName = globbalTargetAccessor ==null? ToLocalVariableName(targetType.Name): ToLocalVariableName(globbalTargetAccessor.ToFullString());
            if (targetExists == false)
            {
                var copyConstructor = FindCopyConstructor(targetType, sourceType);
                if (copyConstructor != null)
                {
                    var init = generator.ObjectCreationExpression(targetType, globalSourceAccessor);
                    if (globbalTargetAccessor == null)
                    {
                        yield return generator.ContextualReturnStatement(init, generatorContext);
                    }
                    else
                    {
                        yield return generator.CompleteAssignmentStatement(globbalTargetAccessor, init);
                    }
                    yield break;
                }
                else
                {
                    var init = generator.ObjectCreationExpression(targetType);
                    yield return generator.LocalDeclarationStatement(targetLocalVariableName, init);     
                }
            }

            var mappingSourceFinder = new MappingSourceFinder(sourceType, globalSourceAccessor, generator);
            var targetProperties = ObjectHelper.GetPublicPropertySymbols(targetType).ToList();
            var localTargetIdentifier = targetExists? globbalTargetAccessor: generator.IdentifierName(targetLocalVariableName);
            foreach (var targetProperty in targetProperties)
            {
                if (targetProperty.SetMethod.DeclaredAccessibility != Accessibility.Public && globbalTargetAccessor.Kind() != SyntaxKind.ThisExpression)
                {
                    continue;
                }

                var mappingSource = mappingSourceFinder.FindMappingSource(targetProperty.Name);
                if (mappingSource == null)
                {
                    continue;
                }

                if (IsMappingBetweenCollections(targetProperty.Type, mappingSource.ExpressionType))
                {
                    var targetAccess = generator.MemberAccessExpression(localTargetIdentifier, targetProperty.Name);
                    var collectionMapping = MapCollections(mappingSource.Expression,  mappingSource.ExpressionType,  targetProperty.Type);
                    yield return generator.CompleteAssignmentStatement(targetAccess, collectionMapping);
                }
                else if (IsSimpleType(targetProperty.Type) == false)
                {   
                    //TODO: What if both sides has the same type?
                    //TODO: Reverse flattening
                    var targetAccess = generator.MemberAccessExpression(localTargetIdentifier, targetProperty.Name);
                    foreach (var complexPropertyMappingNode in MapTypes(mappingSource.ExpressionType, targetProperty.Type, mappingSource.Expression, targetAccess))
                    {
                        yield return complexPropertyMappingNode;
                    }
                }
                else
                {
                    var targetAccess = generator.MemberAccessExpression(localTargetIdentifier, targetProperty.Name);
                    var sourceAccess = mappingSource.Expression as SyntaxNode;
                    if (targetProperty.Type != mappingSource.ExpressionType)
                    {
                        var conversion =  semanticModel.Compilation.ClassifyConversion(mappingSource.ExpressionType, targetProperty.Type);
                        if (conversion.Exists == false)
                        {
                            var wrapper = GetWrappingInfo(mappingSource.ExpressionType, targetProperty.Type);
                            if (wrapper.Type == WrapperInfoType.Property)
                            {
                                sourceAccess = generator.MemberAccessExpression(sourceAccess, wrapper.UnwrappingProperty.Name);
                            }else if (wrapper.Type == WrapperInfoType.Method)
                            {
                                var unwrappingMethodAccess = generator.MemberAccessExpression(sourceAccess, wrapper.UnwrappingMethod.Name);
                                sourceAccess = generator.InvocationExpression(unwrappingMethodAccess);
                            }

                        }else if(conversion.IsExplicit)
                        {
                            sourceAccess = generator.CastExpression(targetProperty.Type, sourceAccess);
                        }
                    }
                    
                    yield return generator.CompleteAssignmentStatement(targetAccess, sourceAccess);
                }
            }

            if (globbalTargetAccessor == null)
            {
                yield return generator.ContextualReturnStatement(localTargetIdentifier, generatorContext);    
            }
            else if(targetExists == false)
            {
                yield return generator.CompleteAssignmentStatement(globbalTargetAccessor, localTargetIdentifier);
            }
        }

        private SyntaxNode MapCollections(SyntaxNode sourceAccess, ITypeSymbol sourceListType, ITypeSymbol targetListType)
        {
            var isReadolyCollection = targetListType.Name == "ReadOnlyCollection";
            var sourceListElementType = GetElementType(sourceListType);
            var targetListElementType = GetElementType(targetListType);
            if (IsSimpleType(sourceListElementType) || sourceListElementType == targetListElementType)
            {
                var toListInvocation = AddMaterializeCollectionInvocation(generator, sourceAccess, targetListType);
                return WrapInReadonlyCollectionIfNecessary(isReadolyCollection, toListInvocation, generator);
            }
            var selectAccess = generator.MemberAccessExpression(sourceAccess, "Select");
            var lambdaParameterName = ToSingularLocalVariableName(ToLocalVariableName(sourceListElementType.Name));
            var listElementMappingStms = MapTypes(sourceListElementType, targetListElementType, generator.IdentifierName(lambdaParameterName));
            var selectInvocation = generator.InvocationExpression(selectAccess, generator.ValueReturningLambdaExpression(lambdaParameterName, listElementMappingStms));
            var toList = AddMaterializeCollectionInvocation(generator, selectInvocation, targetListType);
            return WrapInReadonlyCollectionIfNecessary(isReadolyCollection, toList, generator);
        }

        private static ITypeSymbol GetElementType(ITypeSymbol collectionType)
        {
            switch (collectionType)
            {
                case INamedTypeSymbol namedType:
                    return namedType.TypeArguments[0];
                case IArrayTypeSymbol arrayType:
                    return arrayType.ElementType;
                default:
                    throw new NotSupportedException("Unknown collection type");
            }
        }

        private static SyntaxNode AddMaterializeCollectionInvocation(SyntaxGenerator generator, SyntaxNode sourceAccess, ITypeSymbol targetListType)
        {
            var materializeFunction =  targetListType.Kind == SymbolKind.ArrayType? "ToArray": "ToList";
            var toListAccess = generator.MemberAccessExpression(sourceAccess, materializeFunction );
            return generator.InvocationExpression(toListAccess);
        }

        private static SyntaxNode WrapInReadonlyCollectionIfNecessary(bool isReadonly, SyntaxNode node, SyntaxGenerator generator)
        {
            if (isReadonly == false)
            {
                return node;
            }

            var accessAsReadonly = generator.MemberAccessExpression(node, "AsReadOnly");
            return generator.InvocationExpression(accessAsReadonly);
        }

        private static bool IsMappingBetweenCollections(ITypeSymbol targetClassSymbol, ITypeSymbol sourceClassSymbol)
        {
            return (HasInterface(targetClassSymbol, "System.Collections.Generic.ICollection<T>") || targetClassSymbol.Kind == SymbolKind.ArrayType)
                   && (HasInterface(sourceClassSymbol, "System.Collections.Generic.IEnumerable<T>") || sourceClassSymbol.Kind == SymbolKind.ArrayType);
        }

        private static IMethodSymbol FindCopyConstructor(ITypeSymbol type, ITypeSymbol constructorParameterType)
        {
            if (type is INamedTypeSymbol namedType)
            {
                return namedType.Constructors.FirstOrDefault(c => c.Parameters.Length == 1 && c.Parameters[0].Type == constructorParameterType);
            }
            return null;
        }

        private static bool IsSimpleType(ITypeSymbol type)
        {
            return type.IsValueType || SimpleTypes.Contains(type.Name);
        }

        private static string ToLocalVariableName(string proposalLocalName)
        {
            var withoutForbiddenSigns = string.Join("",proposalLocalName.Trim().Split(FobiddenSigns).Select(x=>
            {
                var cleanElement = x.Trim();
                return $"{cleanElement.Substring(0, 1).ToUpper()}{cleanElement.Substring(1)}";
            }));
            return $"{withoutForbiddenSigns.Substring(0, 1).ToLower()}{withoutForbiddenSigns.Substring(1)}";
        }

        private static string ToSingularLocalVariableName(string proposalLocalName)
        {
            if (proposalLocalName.EndsWith("s"))
            {
                return proposalLocalName.Substring(0, proposalLocalName.Length - 1);
            }

            return proposalLocalName;
        }

        private static bool HasInterface(ITypeSymbol xt, string interfaceName)
        {
            return xt.OriginalDefinition.AllInterfaces.Any(x => x.ToDisplayString() == interfaceName);
        }

        private static WrapperInfo GetWrappingInfo(ITypeSymbol wrapperType, ITypeSymbol wrappedType)
        {
            var unwrappingProperties = ObjectHelper.GetUnwrappingProperties(wrapperType, wrappedType).ToList();
            var unwrappingMethods = ObjectHelper.GetUnwrappingMethods(wrapperType, wrappedType).ToList();
            if (unwrappingMethods.Count + unwrappingProperties.Count == 1)
            {
                if (unwrappingMethods.Count == 1)
                {
                    return new WrapperInfo(unwrappingMethods.First());
                }

                return new WrapperInfo(unwrappingProperties.First());
            }
            return new WrapperInfo();
        }
    }
}