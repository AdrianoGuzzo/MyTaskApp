using Avalonia.Media;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// O celular tem uma cópia dos temas em texto (<see cref="MobileThemeCatalog"/>),
/// porque o núcleo dele não pode depender do Avalonia. Este teste é o que
/// mantém as duas cópias iguais: com as mesmas cores, o contraste medido em
/// <see cref="ThemeContrastTests"/> vale para o celular também.
/// </summary>
public class ThemeParityTests
{
    [Fact]
    public void ThePhone_HasTheSameThemes_InTheSameOrder()
    {
        MobileThemeCatalog.All.Select(theme => (theme.Id, theme.Name, theme.Description, theme.IsDark))
            .Should().Equal(ThemeCatalog.All.Select(theme => (theme.Id, theme.Name, theme.Description, theme.IsDark)));
        MobileThemeCatalog.SystemId.Should().Be(ThemeCatalog.SystemId);
    }

    [Fact]
    public void EveryColor_IsTheDesktopOne()
    {
        var tokens = typeof(ThemePalette).GetProperties().Select(property => property.Name).ToList();

        foreach (var desktop in ThemeCatalog.All)
        {
            var phone = MobileThemeCatalog.Find(desktop.Id)!.Palette.Entries().ToDictionary(entry => entry.Key, entry => entry.Value);

            phone.Keys.Should().BeEquivalentTo(tokens, $"o celular tem as mesmas cores do tema {desktop.Name}");

            foreach (var token in tokens)
            {
                var expected = (Color)typeof(ThemePalette).GetProperty(token)!.GetValue(desktop.Palette)!;

                Color.Parse(phone[token]).Should().Be(expected, $"{desktop.Name}.{token}");
            }
        }
    }
}
