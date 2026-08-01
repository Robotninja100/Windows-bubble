## What changes

Describe the behaviour change, not the diff. If a bug is being fixed, say what
went wrong and why — the mechanism, not just the symptom.

## Judgement calls

Anything you decided that a reviewer could not reconstruct from the code: an
approach you chose over another, something you deliberately left out, a
behaviour change that is intentional. Delete this section if there were none.

## Testing

- [ ] `dotnet test` passes
- [ ] New behaviour has a test, or there is a reason below why it cannot

CI is the only compiler for anyone not on Windows, so "the run is green" is a
legitimate answer here.

### Not verified

CI cannot drive a window or a screen reader. Tick anything this change touches
that still needs a person on Windows, and say whether you checked it:

- [ ] Acrylic blur / the glass itself
- [ ] Keyboard focus ring against dark chrome and glass
- [ ] Narrator announcements
- [ ] Hotkey registration, including from a full-screen app
- [ ] Multi-monitor or mixed-DPI layout
