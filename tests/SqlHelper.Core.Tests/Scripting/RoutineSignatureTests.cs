using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Scripting;

public sealed class RoutineSignatureTests
{
    private static RoutineParameter P(int ord, string name, string type, bool output = false, bool hasDefault = false)
        => new(ord, name, type, IsOutput: output, IsReadOnly: false, HasDefault: hasDefault);

    private static RoutineSignature Sig(params RoutineParameter[] p)
        => new(ProgrammableObjectKind.StoredProcedure, p, ReturnType: null);

    [Fact]
    public void Identical_signatures_compare_equal()
    {
        RoutineSignature a = Sig(P(1, "@id", "int"), P(2, "@name", "varchar"));
        RoutineSignature b = Sig(P(1, "@id", "int"), P(2, "@name", "varchar"));

        SignatureComparison result = a.CompareWith(b);

        Assert.True(result.Identical);
        Assert.Empty(result.Differences);
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void An_added_parameter_is_reported()
    {
        RoutineSignature reference = Sig(P(1, "@id", "int"));
        RoutineSignature client = Sig(P(1, "@id", "int"), P(2, "@includeArchived", "bit", hasDefault: true));

        SignatureComparison result = reference.CompareWith(client);

        Assert.False(result.Identical);
        Assert.Contains(result.Differences, d => d.Contains("@includeArchived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_type_change_is_reported()
    {
        SignatureComparison result = Sig(P(1, "@id", "int")).CompareWith(Sig(P(1, "@id", "bigint")));

        Assert.False(result.Identical);
        Assert.Contains(result.Differences, d => d.Contains("@id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_output_flag_change_is_reported()
    {
        SignatureComparison result = Sig(P(1, "@total", "money")).CompareWith(Sig(P(1, "@total", "money", output: true)));

        Assert.False(result.Identical);
    }

    [Fact]
    public void Hash_is_order_independent_but_content_sensitive()
    {
        RoutineSignature a = Sig(P(1, "@id", "int"), P(2, "@name", "varchar"));
        RoutineSignature reordered = new(
            ProgrammableObjectKind.StoredProcedure,
            [P(2, "@name", "varchar"), P(1, "@id", "int")],
            null);

        Assert.Equal(a.Hash, reordered.Hash);
        Assert.NotEqual(a.Hash, Sig(P(1, "@id", "bigint"), P(2, "@name", "varchar")).Hash);
    }
}
