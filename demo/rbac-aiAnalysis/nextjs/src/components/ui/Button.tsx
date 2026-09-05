"use client";

import React, { JSX } from "react";
import { Spinner } from "./Spinner";

export type ButtonProps = {
  children: React.ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  loading?: boolean;
  variant?: "primary" | "neutral";
  title?: string;
};

export function Button({
  children,
  onClick,
  disabled,
  loading,
  variant = "neutral",
  title,
}: ButtonProps): JSX.Element {
  const isDisabled = Boolean(disabled || loading);

  return (
    <button
      type="button"
      className="ui-button"
      data-variant={variant}
      title={title}
      onClick={onClick}
      disabled={isDisabled}
    >
      {loading ? <Spinner /> : null}
      {children}
    </button>
  );
}
