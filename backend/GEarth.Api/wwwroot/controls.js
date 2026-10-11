(() => {
  const panel = document.getElementById("simulation-controls");
  if (!panel) return;
  const byId = (id) => document.getElementById(id);
  let authenticated = false;
  let busy = false;
  let state = null;
  let enabled = true;
  function render() {
    byId("team-login").hidden = authenticated;
    byId("team-actions").hidden = !authenticated;
    const transition = busy || state?.operation !== null && state?.operation !== undefined;
    byId("simulation-start").disabled = transition || state?.state === "running";
    byId("simulation-stop").disabled = transition || ["paused", "pausing", "stopped"].includes(state?.state);
    byId("simulation-reset").disabled = transition;
    byId("team-logout").disabled = transition;
    if (state) byId("simulation-state").textContent = ({running:"Running continuously",paused:"Stopped · world saved",pausing:"Stopping · saving decisions",stopped:"Stopped",starting:"Starting",error:"Needs attention"})[state.state] || state.state;
  }
  async function request(path, body) {
    const response = await fetch("/api/control/" + path, body === undefined ? {cache:"no-store"} : {method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
    if (response.status === 404) { enabled = false; panel.hidden = true; throw new Error("Controls are available on the cloud website."); }
    const data = await response.json();
    if (!response.ok) {
      if (response.status === 401) authenticated = false;
      throw new Error(typeof data.detail === "string" ? data.detail : "The request could not be completed.");
    }
    return data;
  }
  async function refresh() {
    if (!enabled) return;
    try { state = await request("status"); authenticated = state.authenticated; if (state.error) byId("simulation-message").textContent = state.error; render(); }
    catch (error) { byId("simulation-message").textContent = error.message; render(); }
  }
  byId("team-login").addEventListener("submit", async (event) => {
    event.preventDefault(); if (busy) return;
    busy = true; render();
    try { await request("login", {password:byId("team-password").value}); authenticated = true; byId("simulation-message").textContent = "Team controls unlocked."; }
    catch (error) { byId("simulation-message").textContent = error.message; }
    finally { byId("team-password").value = ""; busy = false; await refresh(); }
  });
  async function action(name) {
    if (busy || !authenticated) return;
    busy = true; render();
    try { await request(name, {}); byId("simulation-message").textContent = name === "stop" ? "Finishing current decisions and saving this world." : name === "reset" ? "Saving the current run and starting a fresh world." : "Simulation resumed. Runs will repeat automatically."; }
    catch (error) { byId("simulation-message").textContent = error.message; }
    finally { busy = false; await refresh(); }
  }
  byId("simulation-start").addEventListener("click", () => action("start"));
  byId("simulation-stop").addEventListener("click", () => action("stop"));
  byId("simulation-reset").addEventListener("click", () => byId("reset-confirmation").showModal());
  byId("reset-confirmation").addEventListener("close", () => { if (byId("reset-confirmation").returnValue === "reset") action("reset"); });
  byId("team-logout").addEventListener("click", async () => { try { await request("logout", {}); authenticated = false; byId("simulation-message").textContent = "Controls locked."; } catch(error) { byId("simulation-message").textContent = error.message; } await refresh(); });
  refresh(); setInterval(refresh, 2000);
})();
