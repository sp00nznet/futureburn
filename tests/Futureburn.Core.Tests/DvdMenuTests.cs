using System.Runtime.Versioning;
using Futureburn.Core.Authoring;

namespace Futureburn.Core.Tests;

[SupportedOSPlatform("windows")]
public class DvdMenuTests
{
    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "fb-menu-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RenderRootMenu_ProducesThreeImagesAndTwoButtons()
    {
        var dir = TempDir();
        try
        {
            var menu = DvdMenuBuilder.RenderRootMenu(
                "The Wrong Trousers", hasScenes: true, isPal: false, dir);

            Assert.True(File.Exists(menu.BackgroundPng));
            Assert.True(File.Exists(menu.HighlightPng));
            Assert.True(File.Exists(menu.SelectPng));
            Assert.Equal(2, menu.Buttons.Count);
            Assert.Equal("play",   menu.Buttons[0].Name);
            Assert.Equal("scenes", menu.Buttons[1].Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RenderRootMenu_OmitsScenesButtonWhenNoChapters()
    {
        var dir = TempDir();
        try
        {
            var menu = DvdMenuBuilder.RenderRootMenu("Movie", hasScenes: false, isPal: false, dir);
            Assert.Single(menu.Buttons);
            Assert.Equal("play", menu.Buttons[0].Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RenderSceneMenu_HasOneButtonPerChapterPlusBack()
    {
        var dir = TempDir();
        try
        {
            var labels = new[] { "Chapter 1", "Chapter 2", "Chapter 3", "Chapter 4", "Chapter 5" };
            var menu = DvdMenuBuilder.RenderSceneMenu(labels, isPal: false, dir);

            Assert.Equal(labels.Length + 1, menu.Buttons.Count);   // + Back
            Assert.Equal("ch1",  menu.Buttons[0].Name);
            Assert.Equal("ch5",  menu.Buttons[4].Name);
            Assert.Equal("back", menu.Buttons[^1].Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RenderSceneMenu_CapsAtMaxSceneButtons()
    {
        var dir = TempDir();
        try
        {
            var labels = Enumerable.Range(1, 30).Select(i => $"Chapter {i}").ToArray();
            var menu = DvdMenuBuilder.RenderSceneMenu(labels, isPal: false, dir);
            Assert.Equal(DvdMenuBuilder.MaxSceneButtons + 1, menu.Buttons.Count);   // cap + Back
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderedButtons_AllHaveEvenCoordinates(bool isPal)
    {
        var dir = TempDir();
        try
        {
            var root  = DvdMenuBuilder.RenderRootMenu("T", hasScenes: true, isPal, dir);
            var scene = DvdMenuBuilder.RenderSceneMenu(
                new[] { "A", "B", "C", "D", "E", "F", "G" }, isPal, dir);
            foreach (var b in root.Buttons.Concat(scene.Buttons))
            {
                Assert.True(b.X0 % 2 == 0 && b.X1 % 2 == 0, $"{b.Name} X not even");
                Assert.True(b.Y0 % 2 == 0 && b.Y1 % 2 == 0, $"{b.Name} Y not even");
                Assert.True(b.X1 > b.X0 && b.Y1 > b.Y0, $"{b.Name} degenerate rect");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

}
