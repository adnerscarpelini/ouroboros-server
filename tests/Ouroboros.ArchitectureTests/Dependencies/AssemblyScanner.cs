using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Ouroboros.ArchitectureTests.Dependencies;

// Tipo de nível mais externo: um tipo aninhado (inclusive closures e state machines geradas pelo compilador)
// conta como o tipo que o contém, que é a unidade que o desenvolvedor reconhece e nomeia nas exceções.
internal readonly record struct TypeId(string Assembly, string Namespace, string Name)
{
    public string FullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";

    public override string ToString() => FullName;
}

internal readonly record struct TypeDependency(TypeId Owner, TypeId Target);

internal sealed record DefinedType(TypeId Id, bool IsExported, string? BaseType);

internal sealed record AssemblyScan(
    string Name,
    ImmutableArray<DefinedType> Types,
    ImmutableArray<string> InternalsVisibleTo,
    ImmutableArray<TypeDependency> Dependencies);

// Lê os metadados e o IL do assembly sem carregá-lo, então não depende de reflexão nem de pacotes. Registra todo tipo
// que cada tipo de src nomeia: base, interfaces, atributos, campos, propriedades, eventos, assinaturas de métodos,
// variáveis locais, cláusulas catch e os operandos de tipo, método e campo das instruções do corpo dos métodos.
// Não resolve argumentos typeof(...) de atributos.
internal static class AssemblyScanner
{
    public static AssemblyScan Scan(string path)
    {
        using var peReader = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
        var reader = peReader.GetMetadataReader();
        var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        var collector = new TypeCollector(reader, assemblyName);

        var types = new List<DefinedType>();
        var dependencies = new HashSet<TypeDependency>();

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var outermost = collector.Outermost(handle);
            if (IsCompilerGenerated(outermost))
            {
                continue;
            }

            if (definition.GetDeclaringType().IsNil)
            {
                types.Add(new DefinedType(outermost, IsExported(reader, definition), BaseTypeName(collector, definition)));
            }

            foreach (var target in TypesUsedBy(reader, peReader, collector, definition))
            {
                if (target != outermost && !IsCompilerGenerated(target))
                {
                    dependencies.Add(new TypeDependency(outermost, target));
                }
            }
        }

        return new AssemblyScan(
            assemblyName,
            [.. types],
            InternalsVisibleToOf(reader, collector),
            [.. dependencies]);
    }

    private static bool IsCompilerGenerated(TypeId type) =>
        type.Name.StartsWith('<')
        || type.Namespace.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
        || type.Namespace.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal);

    private static bool IsExported(MetadataReader reader, TypeDefinition definition)
    {
        while (true)
        {
            var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
            var declaring = definition.GetDeclaringType();
            if (declaring.IsNil)
            {
                return visibility == TypeAttributes.Public;
            }

            if (visibility is not (TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem))
            {
                return false;
            }

            definition = reader.GetTypeDefinition(declaring);
        }
    }

    private static string? BaseTypeName(TypeCollector collector, TypeDefinition definition) =>
        definition.BaseType.IsNil ? null : collector.Resolve(definition.BaseType).FirstOrDefault().FullName;

    private static IEnumerable<TypeId> TypesUsedBy(MetadataReader reader, PEReader peReader, TypeCollector collector, TypeDefinition type)
    {
        var used = new List<TypeId>();

        void Add(EntityHandle handle) => used.AddRange(collector.Resolve(handle));
        void AddAttributes(CustomAttributeHandleCollection attributes)
        {
            foreach (var attribute in attributes)
            {
                Add(reader.GetCustomAttribute(attribute).Constructor);
            }
        }

        if (!type.BaseType.IsNil)
        {
            Add(type.BaseType);
        }

        foreach (var implementation in type.GetInterfaceImplementations())
        {
            Add(reader.GetInterfaceImplementation(implementation).Interface);
        }

        AddAttributes(type.GetCustomAttributes());
        foreach (var parameter in type.GetGenericParameters())
        {
            foreach (var constraint in reader.GetGenericParameter(parameter).GetConstraints())
            {
                Add(reader.GetGenericParameterConstraint(constraint).Type);
            }
        }

        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            used.AddRange(field.DecodeSignature(collector, null));
            AddAttributes(field.GetCustomAttributes());
        }

        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            used.AddRange(Flatten(property.DecodeSignature(collector, null)));
            AddAttributes(property.GetCustomAttributes());
        }

        foreach (var handle in type.GetEvents())
        {
            var @event = reader.GetEventDefinition(handle);
            Add(@event.Type);
            AddAttributes(@event.GetCustomAttributes());
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            used.AddRange(Flatten(method.DecodeSignature(collector, null)));
            AddAttributes(method.GetCustomAttributes());
            foreach (var parameter in method.GetParameters())
            {
                AddAttributes(reader.GetParameter(parameter).GetCustomAttributes());
            }

            if (method.RelativeVirtualAddress != 0)
            {
                used.AddRange(TypesUsedInBody(reader, collector, peReader.GetMethodBody(method.RelativeVirtualAddress)));
            }
        }

        return used;
    }

    private static IEnumerable<TypeId> Flatten(MethodSignature<ImmutableArray<TypeId>> signature) =>
        signature.ParameterTypes.SelectMany(types => types).Concat(signature.ReturnType);

    private static IEnumerable<TypeId> TypesUsedInBody(MetadataReader reader, TypeCollector collector, MethodBodyBlock body)
    {
        var used = new List<TypeId>();

        if (!body.LocalSignature.IsNil)
        {
            foreach (var local in reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(collector, null))
            {
                used.AddRange(local);
            }
        }

        foreach (var region in body.ExceptionRegions.Where(region => !region.CatchType.IsNil))
        {
            used.AddRange(collector.Resolve(region.CatchType));
        }

        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            var first = il.ReadByte();
            var opCode = first == 0xFE ? IlInstructions.TwoByte[il.ReadByte()] : IlInstructions.OneByte[first];
            switch (opCode.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    used.AddRange(collector.Resolve(MetadataTokens.EntityHandle(il.ReadInt32())));
                    break;
                case OperandType.InlineSwitch:
                    // Em duas instruções: "Offset += ReadInt32()" leria o Offset antes de ReadInt32 avançá-lo.
                    var targets = il.ReadInt32();
                    il.Offset += targets * sizeof(int);
                    break;
                default:
                    il.Offset += IlInstructions.OperandSize(opCode.OperandType);
                    break;
            }
        }

        return used;
    }

    private static ImmutableArray<string> InternalsVisibleToOf(MetadataReader reader, TypeCollector collector)
    {
        var friends = ImmutableArray.CreateBuilder<string>();
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (collector.Resolve(attribute.Constructor).FirstOrDefault().FullName != typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute).FullName)
            {
                continue;
            }

            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1 && value.ReadSerializedString() is { } friend)
            {
                friends.Add(new AssemblyName(friend).Name ?? friend);
            }
        }

        return friends.ToImmutable();
    }

    // Converte handles de metadados nos tipos que eles nomeiam. Também é o provedor de assinaturas: tipos genéricos,
    // arrays, ponteiros e modificadores devolvem o tipo e todos os seus argumentos.
    private sealed class TypeCollector(MetadataReader reader, string assemblyName)
        : ISignatureTypeProvider<ImmutableArray<TypeId>, object?>
    {
        public TypeId Outermost(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            while (!definition.GetDeclaringType().IsNil)
            {
                definition = reader.GetTypeDefinition(definition.GetDeclaringType());
            }

            return new TypeId(assemblyName, reader.GetString(definition.Namespace), reader.GetString(definition.Name));
        }

        public ImmutableArray<TypeId> Resolve(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return [Outermost((TypeDefinitionHandle)handle)];
                case HandleKind.TypeReference:
                    return [Reference((TypeReferenceHandle)handle)];
                case HandleKind.TypeSpecification:
                    return reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                case HandleKind.MethodDefinition:
                    return [Outermost(reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType())];
                case HandleKind.FieldDefinition:
                    return [Outermost(reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType())];
                case HandleKind.MemberReference:
                    return Resolve(reader.GetMemberReference((MemberReferenceHandle)handle).Parent);
                case HandleKind.MethodSpecification:
                    var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return [.. Resolve(specification.Method), .. specification.DecodeSignature(this, null).SelectMany(types => types)];
                default:
                    return [];
            }
        }

        private TypeId Reference(TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            while (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                reference = reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope);
            }

            var assembly = reference.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name)
                : assemblyName;
            return new TypeId(assembly, reader.GetString(reference.Namespace), reader.GetString(reference.Name));
        }

        public ImmutableArray<TypeId> GetPrimitiveType(PrimitiveTypeCode typeCode) => [];

        public ImmutableArray<TypeId> GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) =>
            Resolve(handle);

        public ImmutableArray<TypeId> GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) =>
            Resolve(handle);

        public ImmutableArray<TypeId> GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            Resolve(handle);

        public ImmutableArray<TypeId> GetSZArrayType(ImmutableArray<TypeId> elementType) => elementType;

        public ImmutableArray<TypeId> GetArrayType(ImmutableArray<TypeId> elementType, ArrayShape shape) => elementType;

        public ImmutableArray<TypeId> GetByReferenceType(ImmutableArray<TypeId> elementType) => elementType;

        public ImmutableArray<TypeId> GetPointerType(ImmutableArray<TypeId> elementType) => elementType;

        public ImmutableArray<TypeId> GetPinnedType(ImmutableArray<TypeId> elementType) => elementType;

        public ImmutableArray<TypeId> GetModifiedType(ImmutableArray<TypeId> modifier, ImmutableArray<TypeId> unmodifiedType, bool isRequired) =>
            [.. modifier, .. unmodifiedType];

        public ImmutableArray<TypeId> GetGenericInstantiation(ImmutableArray<TypeId> genericType, ImmutableArray<ImmutableArray<TypeId>> typeArguments) =>
            [.. genericType, .. typeArguments.SelectMany(types => types)];

        public ImmutableArray<TypeId> GetFunctionPointerType(MethodSignature<ImmutableArray<TypeId>> signature) =>
            [.. Flatten(signature)];

        public ImmutableArray<TypeId> GetGenericMethodParameter(object? genericContext, int index) => [];

        public ImmutableArray<TypeId> GetGenericTypeParameter(object? genericContext, int index) => [];
    }
}

// Tabela das instruções do IL, indexada pelo primeiro (ou segundo, após 0xFE) byte do código.
internal static class IlInstructions
{
    public static readonly OpCode[] OneByte = new OpCode[256];

    public static readonly OpCode[] TwoByte = new OpCode[256];

    static IlInstructions()
    {
        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            (opCode.Size == 1 ? OneByte : TwoByte)[opCode.Value & 0xFF] = opCode;
        }
    }

    public static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };
}
