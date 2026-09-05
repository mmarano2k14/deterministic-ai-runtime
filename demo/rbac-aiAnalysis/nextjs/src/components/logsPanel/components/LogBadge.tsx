import { JSX } from "react";
import { LogBadge as LogBadgeModel } from "../LogsPanelType";

export type LogBadgeProps = {
  badge: LogBadgeModel;
};

export function LogBadge(props: LogBadgeProps): JSX.Element {
  const { badge } = props;

  return (
    <span
      className="log-badge"
      data-tone={badge.tone}
    >
      {badge.label}
    </span>
  );
}
