"""Add narrow pause/UI hooks without replacing the collecting engine or observer."""

PANEL = '''
      <section id="simulation-controls" class="simulation-controls" aria-labelledby="simulation-controls-title">
        <div class="control-heading"><h2 id="simulation-controls-title">Shared simulation</h2><span id="simulation-state" role="status">Connecting</span></div>
        <p>Runs repeat automatically. Stop pauses this world; Start resumes it. Reset starts a fresh world and keeps saved runs.</p>
        <form id="team-login"><label for="team-password">Team password</label><input id="team-password" type="password" autocomplete="current-password" required maxlength="256"><button type="submit">Unlock controls</button></form>
        <div id="team-actions" hidden><button id="simulation-start" type="button">Start</button><button id="simulation-stop" type="button">Stop</button><button id="simulation-reset" type="button">Reset</button><button id="team-logout" type="button">Lock controls</button></div>
        <p id="simulation-message" role="status" aria-live="polite"></p>
        <dialog id="reset-confirmation"><form method="dialog"><h2>Reset this world?</h2><p>The current run will be saved, and a fresh world will start. Everyone shares this simulation.</p><button value="cancel">Cancel</button><button value="reset">Reset simulation</button></form></dialog>
      </section>
'''


def pause_hooks(source):
    if "new SimulationPause(" in source:
        return source
    replacements = [
        ("var queue = new Queue<string>();", 'var queue = new Queue<string>();\n        var pause = new SimulationPause(Environment.GetEnvironmentVariable("FAULTLINE_CONTROL_DIRECTORY"));'),
        ("void Dispatch()\n        {", "void Dispatch()\n        {\n            if (pause.Requested)\n                return;"),
        ("        async Task DrainWeek()", '''        async Task PauseIfRequested()
        {
            await pause.WaitAsync(async () =>
            {
                while (pending.Count > 0)
                {
                    await pending.WaitForCompletion(cancellationToken);
                    ApplyCompleted();
                }
                await Commit();
                await notes.Record(current!, "paused");
            }, cancellationToken);
        }

        async Task DrainWeek()'''),
        ("                cancellationToken.ThrowIfCancellationRequested();\n                ApplyCompleted();", "                cancellationToken.ThrowIfCancellationRequested();\n                await PauseIfRequested();\n                ApplyCompleted();"),
        ("                var started = DateTimeOffset.UtcNow;", "                await PauseIfRequested();\n                var started = DateTimeOffset.UtcNow;"),
    ]
    for old, new in replacements:
        if source.count(old) != 1:
            raise ValueError("Unsupported coordinator pause hook; review source before deploying")
        source = source.replace(old, new)
    return source


def control_panel(source):
    if 'id="simulation-controls"' in source:
        return source
    if source.count("</head>") != 1 or source.count("<main>") != 1:
        raise ValueError("Unsupported observer layout; review before deploying")
    return source.replace("</head>", '<link rel="stylesheet" href="/controls.css" />\n    <script src="/controls.js" defer></script>\n  </head>').replace("<main>", "<main>" + PANEL)
