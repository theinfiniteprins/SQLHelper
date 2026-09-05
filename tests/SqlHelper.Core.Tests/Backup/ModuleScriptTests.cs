using SqlHelper.Core.Backup;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Backup;

public sealed class ModuleScriptTests
{
    private static ProgrammableObject Module(string definition, bool ansiNulls = true, bool quotedId = true) => new(
        new ObjectName("dbo", "usp_X"),
        ProgrammableObjectKind.StoredProcedure,
        definition,
        ansiNulls,
        quotedId,
        ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow,
        ModifiedServerTime: DateTime.UtcNow,
        IsEncrypted: false);

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1")]
    public void AsCreateOrAlter_normalises_the_verb(string definition)
    {
        Assert.StartsWith("CREATE OR ALTER PROCEDURE", ModuleScript.AsCreateOrAlter(definition));
    }

    [Fact]
    public void ToRunnableScript_carries_the_captured_set_options()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1", ansiNulls: false, quotedId: true));

        Assert.Contains("SET ANSI_NULLS OFF", script);
        Assert.Contains("SET QUOTED_IDENTIFIER ON", script);
    }

    [Fact]
    public void ToRunnableScript_batches_each_part_with_GO()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1"));

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [Fact]
    public void ToRunnableScript_forces_create_or_alter_by_default()
    {
        string script = ModuleScript.ToRunnableScript(Module("CREATE PROCEDURE dbo.x AS SELECT 1"));

        Assert.Contains("CREATE OR ALTER PROCEDURE", script);
    }
}
