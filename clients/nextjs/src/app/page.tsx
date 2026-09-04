"use client";

import React, { JSX, useState } from "react";
import { useRouter } from "next/navigation";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { useConsoleContext } from "@/lib/console/contextProvider/useConsoleContext";

const demoFlow = [
  {
    step: "01",
    title: "Observe",
    text: "RBAC, atomic ContextKey rotation, in-flight traffic, realtime evidence.",
  },
  {
    step: "02",
    title: "Analyze",
    text: "AI turns bounded runtime evidence into a structured proposal.",
  },
  {
    step: "03",
    title: "Decide",
    text: "Pluggable policy gates the proposal; a human approves or rejects.",
  },
  {
    step: "04",
    title: "Execute & verify",
    text: "The runtime executes durably and evidence verifies the result.",
  },
] as const;

export default function LoginPage(): JSX.Element {
  const router = useRouter();
  const controller = useConsoleContext();

  const [username, setUsername] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function login(e: React.FormEvent<HTMLFormElement>): Promise<void> {
    e.preventDefault();
    setError(null);

    try {
      const can = await controller.actions.login(username.trim());

      if (!can) {
        setError("Login failed.");
        return;
      }

      router.push("/dashboard");
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "Login failed.");
    }
  }

  return (
    <div className="login-page login-page--compact">
      <div className="login-page__background" aria-hidden="true">
        <div className="login-page__grid" />
        <div className="login-page__glow login-page__glow--blue" />
        <div className="login-page__glow login-page__glow--violet" />
        <div className="login-page__glow login-page__glow--green" />
      </div>

      <main className="login-shell login-shell--compact">
        <section className="login-shell__brand login-shell__brand--compact">
          <div className="login-shell__eyebrow">
            AI Runtime Analysis Demo · Powered by Deterministic AI Runtime
          </div>

          <h1 className="login-shell__title login-shell__title--compact">
            Observe. Analyze. Decide.
            <span className="login-shell__title-accent">
              {" "}
              Verify.
            </span>
          </h1>

          <p className="login-shell__subtitle login-shell__subtitle--compact">
            A small demo application built on top of the Deterministic AI
            Runtime — not the runtime itself. It uses real runtime extension
            points to exercise pluggable steps and policies, durable Child
            DAGs, AI-generated proposals, human approval, recovery, and
            verification.
          </p>

          <div
            className="login-shell__chips login-shell__chips--compact"
            aria-label="Demo capabilities"
          >
            <span className="login-chip">RBAC</span>
            <span className="login-chip">ContextKey rotation</span>
            <span className="login-chip">In-flight continuity</span>
            <span className="login-chip">Redis / Lua</span>
            <span className="login-chip">AI analysis</span>
            <span className="login-chip">Durable Child DAG</span>
          </div>

          <div
            className="login-flow login-flow--compact"
            aria-label="Demo decision flow"
          >
            {demoFlow.map((item) => (
              <article className="login-flow__item" key={item.step}>
                <div className="login-flow__step">{item.step}</div>
                <div className="login-flow__content">
                  <strong className="login-flow__title">{item.title}</strong>
                  <span className="login-flow__text">{item.text}</span>
                </div>
              </article>
            ))}
          </div>

          <div className="login-decision-strip">
            <span>AI proposes</span>
            <i aria-hidden="true">→</i>
            <span>Policy gates</span>
            <i aria-hidden="true">→</i>
            <span>Human approves / rejects</span>
            <i aria-hidden="true">→</i>
            <span>Runtime executes</span>
            <i aria-hidden="true">→</i>
            <span>Evidence verifies</span>
          </div>

          <div className="login-investigation">
            <div className="login-investigation__header">
              Investigation modes
            </div>

            <div className="login-investigation__modes">
              <div className="login-investigation__mode">
                <span className="login-investigation__dot" />
                <div>
                  <strong>Stop when conclusion is strong</strong>
                  <span>Default: stop once evidence is conclusive.</span>
                </div>
              </div>

              <div className="login-investigation__mode">
                <span className="login-investigation__dot" />
                <div>
                  <strong>Continue with another useful experiment</strong>
                  <span>
                    A materially different proposal must pass policy and human
                    approval again before the next Child DAG.
                  </span>
                </div>
              </div>
            </div>
          </div>
        </section>

        <div className="login-access-column">
          <form className="login-card login-card--compact" onSubmit={login}>
            <div className="login-card__header">
              <div className="login-card__badge">
                Demo Application
              </div>

              <h2 className="login-card__title">
                Open the demo
              </h2>

              <p className="login-card__text">
                Start an isolated session and explore the execution,
                investigation, approval, and verification loop.
              </p>
            </div>

            <div className="login-scope-list">
              <div className="login-scope-row">
                <strong>Traffic</strong>
                <span>
                  RBAC · ContextKey rotation · in-flight continuity
                </span>
              </div>

              <div className="login-scope-row">
                <strong>Coordination</strong>
                <span>
                  Redis/Lua · realtime evidence · metrics
                </span>
              </div>

              <div className="login-scope-row">
                <strong>Decision</strong>
                <span>
                  AI proposal · pluggable policy · human approval
                </span>
              </div>

              <div className="login-scope-row">
                <strong>Durability</strong>
                <span>
                  Child DAG · lifecycle · recovery · verification
                </span>
              </div>
            </div>

            <div className="login-form">
              <div className="login-field">
                <label htmlFor="username">Demo username</label>
                <input
                  id="username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  placeholder="marco"
                  autoComplete="username"
                  disabled={controller.state.busy}
                />
              </div>

              {error && <div className="login-error">{error}</div>}

              <button
                type="submit"
                className="login-submit"
                disabled={
                  controller.state.busy || username.trim().length === 0
                }
              >
                {controller.state.busy
                  ? "Starting demo..."
                  : "Launch demo"}
              </button>
            </div>

            <div className="login-card__footer">
              <span className="login-card__hint">
                The demo uses the runtime&apos;s real public primitives and
                extension points. Durable execution remains the responsibility
                of the Deterministic AI Runtime.
              </span>
            </div>
          </form>

          <div className="login-theme-below">
            <ThemeToggle />
          </div>
        </div>
      </main>
    </div>
  );
}
