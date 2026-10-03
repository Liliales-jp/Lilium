using Lilium;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
var bindings = BindingRules.Defaults();
string? Reason(string action, InputGesture input) => PreviewRules.DisabledInputReason(action, input, bindings);
foreach (int key in new[] { 13, 32, 49, 50 })
    Check(Reason("NextRight", new(key)) == "Preview_ConflictingKey", $"List binding {key} cannot turn pages");
foreach (int key in new[] { 33, 34, 35, 36, 37, 38, 39, 40, 46, 113, 116 })
    Check(Reason("StepNext", new(key)) == "Preview_ReservedKey", $"Standard list key {key} cannot turn pages");
foreach (var input in new[] { new InputGesture(65, KeyModifiers.Control), new InputGesture(78, KeyModifiers.Control | KeyModifiers.Shift) })
    Check(Reason("StepNext", input) == "Preview_ReservedKey", "File operation gestures cannot turn pages");
foreach (int key in new[] { 65, 68, 83, 87 })
    Check(Reason("StepNext", new(key)) is null, $"Nonconflicting reader key {key} is available");
bindings["Rating0"].Add(new(83));
Check(Reason("StepNext", new(83)) == "Preview_ConflictingKey", "Custom library keys suppress preview input");
Check(Reason("StepNext", new(83, KeyModifiers.Shift)) is null, "Modifiers match exactly");
bindings["Open"].Remove(new(32));
Check(Reason("NextRight", new(32)) is null, "Removing a library assignment enables reader input");
foreach (var input in new[] { new InputGesture(27), new InputGesture(81), new InputGesture(Mouse: "Left"), new InputGesture(Mouse: "WheelDown") })
    Check(Reason("CloseReader", input) == "Preview_CloseDisabled", "Close is disabled for every mapping");
Check(Reason("NextRight", new(Mouse: "Back")) == "Preview_CloseDisabled", "Mouse Back never navigates within preview");
foreach (var mouse in new[] { "Left", "Right", "WheelDown", "WheelUp", "Forward", "Middle" })
    Check(Reason("NextRight", new(Mouse: mouse)) is null, $"Pointer surface isolates {mouse} from library bindings");

Check(PreviewRules.FitWidth(480, 1200) == 480, "Default width is retained when there is room");
Check(PreviewRules.FitWidth(900, 1200) == 872, "List minimum remains available");
Check(PreviewRules.FitWidth(100, 1200) == 320, "Preview minimum is enforced");
Check(PreviewRules.FitWidth(480, 500) == 246, "Narrow windows share their space");
for (int available = 0; available <= 2000; available += 13)
{
    double fitted = PreviewRules.FitWidth(480, available);
    Check(double.IsFinite(fitted) && fitted >= 0 && fitted <= Math.Max(0, available - 8), "Width never overflows its host");
}
Check(PreviewRules.FitWidth(double.NaN, 1200) == 480, "Invalid preferred widths use the default");

var requests = new ViewerLoadRequests();
using var first = requests.Begin()!;
using var second = requests.Begin()!;
Check(first.CancellationToken.IsCancellationRequested, "New target cancels the old page");
Check(!requests.IsCurrent(first) && requests.IsCurrent(second), "Only the newest page can commit");
Check(!requests.Complete(first) && requests.IsCurrent(second), "Late completion cannot clear the new request");
requests.Cancel();
Check(second.CancellationToken.IsCancellationRequested && !requests.IsCurrent(second), "Clearing selection cancels and invalidates its page");
using var reopened = requests.Begin()!;
Check(requests.IsCurrent(reopened), "A collapsed reader can be reopened");
requests.Close();
Check(reopened.CancellationToken.IsCancellationRequested && requests.Begin() is null, "Closed readers cannot restart");

bool? Portrait(int index) => index == 2 ? false : true;
Check(ViewerPagePlanner.SpreadLength(5, 0, "two", Portrait) == 2, "Portrait pages share a spread");
Check(ViewerPagePlanner.NextIndex(5, 0, "two", Portrait) == 2, "Next moves past the current spread");
Check(ViewerPagePlanner.SpreadLength(5, 2, "two", Portrait) == 1, "Landscape pages stay single");
Check(ViewerPagePlanner.PreviousIndex(5, 2, "two", Portrait) == 0, "Previous returns to the full prior spread");
Check(ViewerPagePlanner.SpreadLength(5, 4, "two", Portrait) == 1, "Odd final pages stay visible");
Check(ViewerPagePlanner.NextIndex(5, 4, "two", Portrait) == 4, "The last page does not loop or close");
Check(ViewerPagePlanner.NextIndex(5, 0, "one", Portrait) == 1, "Single-page mode advances by one");
Console.WriteLine($"Preview checks passed: {checks}");
