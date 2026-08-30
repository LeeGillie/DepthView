namespace DepthView.Views;

/// <summary>One person, project or specification the roll thanks.</summary>
/// <param name="Name">The line shown in normal weight.</param>
/// <param name="Note">The smaller line under it, saying what they contributed.</param>
public readonly record struct CreditEntry(string Name, string Note);

/// <summary>A headed block of the credit roll.</summary>
public readonly record struct CreditSection(string Role, CreditEntry[] Entries);

/// <summary>
/// The contents of the About box credit roll. Kept as data rather than markup so the
/// roll can be measured, looped and reused by a text export without touching the layout.
/// </summary>
public static class Credits
{
    public static readonly CreditSection[] Sections =
    {
        new("AUTHOR AND DIRECTION", new CreditEntry[]
        {
            new("Lee Gillie",
                "Conceived DepthView and specified every capability in it: the imposter test, "
              + "the grey level census, the endpoint report, the lit relief preview and the "
              + "focus on what a laser will actually cut. Brought the depth map and engraving "
              + "domain knowledge, supplied the real files that exposed the bugs, and judged "
              + "each build against them."),
        }),

        new("IMPLEMENTATION", new CreditEntry[]
        {
            new("Claude, by Anthropic",
                "Wrote the implementation across a series of working sessions with Lee: the "
              + "bit exact PNG, Netpbm and PFM decoders, the imposter classifier, the software "
              + "relief renderer and its material system, the Avalonia interface, the icon and "
              + "banner artwork, the fixture generators and the documentation."),
            new("How the work was divided",
                "Lee decided what the tool should do, whether it did it, and what was worth "
              + "doing next. Claude turned those decisions into code and put each result back "
              + "in front of him. Several of the sharpest bugs in this program were found "
              + "because Lee looked at a render and said it was wrong."),
        }),

        new("BUILT WITH", new CreditEntry[]
        {
            new("Avalonia UI",
                "Cross platform .NET user interface toolkit. Draws its own widgets, which is "
              + "why one codebase looks and behaves the same on Windows, macOS and Linux. MIT."),
            new(".NET and the C# language, Microsoft",
                "Runtime and compiler. Self contained publishing is what lets a release be a "
              + "single file with nothing to install. MIT."),
            new("SixLabors.ImageSharp",
                "Decodes TIFF, JPEG, BMP, WebP, GIF, TGA and QOI, and encodes the PNG output of "
              + "the render path. Six Labors Split Licence, used here under its open source grant."),
            new("Inter, by Rasmus Andersson",
                "The typeface this interface is set in, embedded in the binary so the program "
              + "looks the same on a machine that has never seen it. SIL Open Font Licence."),
            new("Python, NumPy and Pillow",
                "Generate the test fixtures whose correct answers are known by construction, and "
              + "the program artwork. The fixture writer is hand rolled so a fixture cannot "
              + "inherit a bug from the same kind of library DepthView exists to distrust."),
            new("Playwright and Chromium",
                "Typeset the project banner. Used only in the artwork pipeline; no part of it "
              + "ships in the program."),
        }),

        new("STANDARDS READ RATHER THAN GUESSED", new CreditEntry[]
        {
            new("The PNG specification, W3C and ISO/IEC 15948",
                "Every bit depth, every colour type, Adam7 interlacing and all five scanline "
              + "filters are implemented from the specification, because the platform image "
              + "loaders quietly hand back eight bits for a sixteen bit file."),
            new("RFC 1950 and RFC 1951, zlib and DEFLATE",
                "The compressed stream underneath every PNG."),
            new("The Netpbm formats, and PFM",
                "PGM, PPM, PBM at any maximum value, and floating point depth maps."),
        }),

        new("WITH THANKS TO", new CreditEntry[]
        {
            new("Victor Wolansky",
                "For the Dolly Parton memorial coin relief, made and shared freely with the "
              + "engraving community. It was one of the real depth maps DepthView was tested "
              + "against, and real work made by someone who was not trying to produce a test "
              + "case is worth more to a tool like this than anything written to order. "
              + "The file itself is his and is not distributed here."),
            new("LightBurn Software",
                "For 3D Sliced Image mode, and for documentation candid enough to say where its "
              + "depth control ends. DepthView is built to answer the questions that leaves open."),
            new("The WeCreat Lumos Ultra community",
                "For working out in public what a 256 layer relief actually needs from its source "
              + "image, long before any tool was checking."),
            new("Isotope NW",
                "The visual language the project banner is drawn in."),
            new("Everyone who has ever ruined a workpiece",
                "and only then discovered the depth map was eight bits in a sixteen bit coat. "
              + "This program exists so that the discovery happens earlier and costs less."),
        }),

        new("LICENCE", new CreditEntry[]
        {
            new("MIT",
                "Copyright (c) 2026 Lee Gillie. Free to use, change and redistribute. "
              + "Dependency licences are listed in THIRD-PARTY-NOTICES.md, and in full "
              + "behind the Licence button below."),
        }),
    };
}
