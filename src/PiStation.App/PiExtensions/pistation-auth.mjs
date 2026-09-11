// The provider owns authentication; only native prompt answers enter its callbacks.
// Never return provider exception text: it can echo a credential or callback URL.
export async function loginDesktopProvider(runtime, providerId, authType, ui, openUrl, timeoutMs = 8 * 60 * 1000) {
  if (!["oauth", "api_key"].includes(authType)) throw new Error("Choose browser or API-key sign-in.");
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), timeoutMs);
  try {
    await runtime.login(providerId, authType, {
      signal: abort.signal,
      prompt: async prompt => {
        const answer = prompt.type === "select"
          ? await ui.select(prompt.message, prompt.options.map(option => option.label), { signal: abort.signal, timeout: timeoutMs })
          : await ui.input(prompt.message, "[pistation:secret]", { signal: abort.signal, timeout: timeoutMs });
        if (answer === undefined || abort.signal.aborted) { abort.abort(); throw new Error("Sign-in cancelled."); }
        if (prompt.type !== "select") return answer;
        const selected = prompt.options.find(option => option.label === answer);
        if (!selected) throw new Error("Invalid sign-in choice.");
        return selected.id;
      },
      notify: event => {
        if (abort.signal.aborted) return;
        const value = event.type === "auth_url" ? event.url : event.verificationUri;
        let url;
        try { const parsed = new URL(value); if (parsed.protocol === "https:" && !parsed.username && !parsed.password) url = parsed.href; } catch { }
        // Ignore free-form provider messages, which can include credentials.
        if (url) {
          ui.setStatus("Pi sign-in", event.userCode ? `Code: ${String(event.userCode).slice(0, 128)} · ${url}` : url);
          openUrl(url);
        }
      },
    });
    if (abort.signal.aborted) throw new Error("Sign-in cancelled.");
  } catch {
    ui.setStatus("Pi sign-in", undefined);
    throw new Error(abort.signal.aborted ? "Sign-in cancelled or timed out." : "Provider sign-in failed. Check the provider and retry; credential details are not displayed.");
  } finally { clearTimeout(timer); abort.abort(); }
}
