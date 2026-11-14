// Loaded in <head>: applies the saved theme before first paint to avoid a flash.
try {
  const saved = localStorage.getItem("trignis_theme");
  if (saved === "light" || saved === "dark")
    document.documentElement.setAttribute("data-theme", saved);
} catch {
  /* Private mode: system preference applies. */
}

// data-theme is the applied choice, valid without storage. No attribute: the system preference decides.
function effectiveTheme() {
  const applied = document.documentElement.getAttribute("data-theme");
  if (applied === "light" || applied === "dark") return applied;
  return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function cycleTheme() {
  const theme = effectiveTheme() === "dark" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", theme);
  try { localStorage.setItem("trignis_theme", theme); } catch {}
  syncThemeToggle();
}

function syncThemeToggle() {
  const btn = document.getElementById("themeToggle");
  if (!btn) return;
  const theme = effectiveTheme();
  btn.dataset.mode = theme;
  btn.setAttribute("aria-label", theme === "dark" ? "Switch to light theme" : "Switch to dark theme");
  btn.title = btn.getAttribute("aria-label");
}

matchMedia("(prefers-color-scheme: dark)").addEventListener("change", syncThemeToggle);
document.addEventListener("DOMContentLoaded", syncThemeToggle);
