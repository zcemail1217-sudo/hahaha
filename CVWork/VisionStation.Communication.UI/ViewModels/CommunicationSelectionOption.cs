namespace VisionStation.Communication.UI.ViewModels;

public sealed record TextSelectionOption(string Value, string Text)
{
    public override string ToString()
    {
        return Text;
    }
}
