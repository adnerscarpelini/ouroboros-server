using System.Text;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Testes de unidade da política de senha (NIST SP 800-63B-4); não usam banco.
public sealed class PasswordPolicyTests
{
    private const string Email = "oi@adnerscarpelini.com";

    [Fact]
    public void Password_with_14_characters_is_rejected_with_minimum_length() =>
        Assert.Contains("15", PasswordPolicy.Validate(new string('k', 6) + "abcdefgh", Email));

    [Fact]
    public void Password_with_15_characters_is_accepted() =>
        Assert.Null(PasswordPolicy.Validate("girafa azul voa", Email));

    [Fact]
    public void Password_with_128_code_points_is_accepted() =>
        Assert.Null(PasswordPolicy.Validate(string.Concat(Enumerable.Repeat("ação ", 25)) + "fim", Email));

    [Fact]
    public void Password_with_129_code_points_is_rejected_not_truncated() =>
        Assert.Contains("128", PasswordPolicy.Validate(string.Concat(Enumerable.Repeat("ação ", 25)) + "fimx", Email));

    // Emojis ocupam 2 unidades UTF-16 cada, mas contam 1 code point.
    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Length_is_counted_in_code_points(int emojiCount, bool accepted)
    {
        var emojis = string.Concat(Enumerable.Repeat("\U0001F600", emojiCount));

        Assert.Equal(accepted, PasswordPolicy.Validate(emojis, Email) is null);
    }

    // A ligadura "ﬁ" (1 code point) vira "fi" (2) no NFKC: 14 caracteres digitados passam a 15.
    [Fact]
    public void Length_is_counted_after_nfkc() =>
        Assert.Null(PasswordPolicy.Validate("ﬁ" + "lhote azul ok", Email));

    // Caracteres fullwidth viram ASCII no NFKC, então o nome do sistema também é encontrado.
    [Fact]
    public void Product_name_is_found_after_nfkc() =>
        Assert.NotNull(PasswordPolicy.Validate("minha senha ＯＵＲＯＢＯＲＯＳ forte", Email));

    [Theory]
    [InlineData("passwordpassword")]
    [InlineData("PASSWORDPASSWORD")]
    [InlineData("PassWordPassWord")]
    [InlineData("Correct Horse Battery Staple")]
    public void Common_passwords_are_rejected_in_any_case(string password) =>
        Assert.Contains("comuns", PasswordPolicy.Validate(password, Email));

    [Fact]
    public void Passphrase_with_spaces_and_accents_is_accepted() =>
        Assert.Null(PasswordPolicy.Validate("café coração e pão de queijo", Email));

    [Theory]
    [InlineData("minha senha do OUROBOROS")]
    [InlineData("ouroborosouroboros")]
    public void Password_containing_product_name_is_rejected(string password) =>
        Assert.NotNull(PasswordPolicy.Validate(password, Email));

    [Fact]
    public void Password_containing_email_local_part_with_four_or_more_characters_is_rejected() =>
        Assert.NotNull(PasswordPolicy.Validate("eu sou o joao.silva mesmo", "joao.silva@adnerscarpelini.com"));

    [Fact]
    public void Short_email_local_part_is_not_checked() =>
        Assert.Null(PasswordPolicy.Validate("oi oi oi bom dia sol", Email));

    // A mesma senha digitada com acento composto ou decomposto verifica igual (NFKC antes do hash).
    [Fact]
    public void Composed_and_decomposed_accents_verify_the_same()
    {
        var hasher = new AspNetCorePasswordHasher();
        var composed = "coração valente forte".Normalize(NormalizationForm.FormC);
        var decomposed = composed.Normalize(NormalizationForm.FormD);

        Assert.NotEqual(composed, decomposed);
        Assert.Equal(PasswordVerificationOutcome.Succeeded, hasher.Verify(hasher.Hash(composed), decomposed));
        Assert.Equal(PasswordVerificationOutcome.Succeeded, hasher.Verify(hasher.Hash(decomposed), composed));
    }
}
