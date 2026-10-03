namespace DepthView.Processing;

// The two wizard answers that are remembered between sessions (Preferences.WizardMemory).
// They live in a file of their own, with no dependencies, because tests/updater compiles
// Preferences.cs on its own, without the rest of the program.

/// <summary>What will turn the map into a job. Decides the defaults, never the measurements.</summary>
public enum WizardTarget
{
    /// <summary>WeCreat MakeIt, Relief (Emboss): 8 bits, at most 256 layers, Z 0.01 mm a layer.</summary>
    MakeIt,

    /// <summary>LightBurn on a G-code machine: an Image layer in Grayscale mode, no slicing.</summary>
    LightBurn,

    /// <summary>A slicer that cuts one band of levels per pass: LightBurn 3D Slice on a galvo, and the like.</summary>
    Slicer,
}

/// <summary>Which nearly level areas the wizard offers to change.</summary>
public enum FlatScope { None, Floor, All }
