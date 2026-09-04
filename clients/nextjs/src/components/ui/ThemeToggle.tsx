"use client";

import { JSX, useEffect, useState } from "react";

type ConsoleTheme = "light" | "dark";

const THEME_STORAGE_KEY = "deterministic-ai-runtime-theme";

function documentTheme(): ConsoleTheme {
  return document.documentElement.dataset.theme === "dark"
    ? "dark"
    : "light";
}

export function ThemeToggle(): JSX.Element {
  const [theme, setTheme] = useState<ConsoleTheme>("light");

  useEffect(() => {
    setTheme(documentTheme());
  }, []);

  function handleToggle(): void {
    const nextTheme: ConsoleTheme =
      documentTheme() === "dark" ? "light" : "dark";

    document.documentElement.classList.add("theme-is-transitioning");
    document.documentElement.dataset.theme = nextTheme;
    localStorage.setItem(THEME_STORAGE_KEY, nextTheme);
    setTheme(nextTheme);

    window.setTimeout(() => {
      document.documentElement.classList.remove("theme-is-transitioning");
    }, 280);
  }

  const isDark = theme === "dark";

  return (
    <div className="theme-control">
      <span className="theme-control__label">Theme</span>

      <button
        type="button"
        className="theme-toggle"
        data-theme={theme}
        role="switch"
        aria-checked={isDark}
        aria-label={
          isDark
            ? "Switch to light mode"
            : "Switch to dark mode"
        }
        title={
          isDark
            ? "Switch to light mode"
            : "Switch to dark mode"
        }
        onClick={handleToggle}
      >
        <span
          className="theme-toggle__icon theme-toggle__icon--sun"
          aria-hidden="true"
        >
          <svg
            viewBox="0 0 24 24"
            focusable="false"
          >
            <circle cx="12" cy="12" r="3.5" />
            <path d="M12 2.5v2M12 19.5v2M4.57 4.57l1.42 1.42M18.01 18.01l1.42 1.42M2.5 12h2M19.5 12h2M4.57 19.43l1.42-1.42M18.01 5.99l1.42-1.42" />
          </svg>
        </span>

        <span
          className="theme-toggle__icon theme-toggle__icon--moon"
          aria-hidden="true"
        >
          <svg
            viewBox="0 0 24 24"
            focusable="false"
          >
            <path d="M20.2 15.35A8.25 8.25 0 0 1 8.65 3.8 8.25 8.25 0 1 0 20.2 15.35Z" />
          </svg>
        </span>

        <span className="theme-toggle__thumb" aria-hidden="true">
          <span className="theme-toggle__thumb-glow" />
        </span>
      </button>
    </div>
  );
}
