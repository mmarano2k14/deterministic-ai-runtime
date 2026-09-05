export function StatusItem({
  label,
  value,
  mono,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div className="console-status__item">
      <span className="console-status__label">{label}:</span>
      <span
        className={`console-status__value ${
          mono ? "console-status__value--mono" : ""
        }`}
      >
        {value}
      </span>
    </div>
  );
}