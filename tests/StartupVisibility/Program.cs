using ClawLibrary.Engine;
int count = 0;
void Check(bool value) { if (!value) throw new Exception("Visibility gate regression"); count++; }
var gate = new StartupVisibilityGate();
Check(gate.Observe(0, false, false) == StartupReveal.Hold);
Check(gate.Observe(120000, false, true) == StartupReveal.Hold);
Check(gate.Observe(120001, true, true) == StartupReveal.Hold);
Check(gate.Observe(121000, true, true) == StartupReveal.Hold);
Check(gate.Observe(121101, true, true) == StartupReveal.Ready);
Check(gate.Observe(121200, false, true) == StartupReveal.Hold);
Check(gate.Observe(121300, true, true) == StartupReveal.Hold);
Check(gate.Observe(122400, true, true) == StartupReveal.Ready);
var delayed = new StartupVisibilityGate();
Check(delayed.Observe(900000, false, false) == StartupReveal.Hold);
Check(delayed.Observe(900001, true, false) == StartupReveal.Hold);
Check(delayed.Observe(901500, true, false) == StartupReveal.Hold);
Check(delayed.Observe(945001, true, false) == StartupReveal.Timeout);
Console.WriteLine($"PASS: {count} hidden-desktop and visible-duration checks");
