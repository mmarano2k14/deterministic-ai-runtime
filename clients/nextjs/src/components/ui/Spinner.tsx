"use client";

import { JSX } from "react";

export type SpinnerProps = {
  size?: 16 | 18;
};

export function Spinner({ size = 16 }: SpinnerProps): JSX.Element {
  return (
    <span
      aria-label="loading"
      className={`ui-spinner ui-spinner--${size}`}
    />
  );
}
