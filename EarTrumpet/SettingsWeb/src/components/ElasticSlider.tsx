import { animate, motion, useMotionValue, useReducedMotion, useTransform } from "motion/react";
import { useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent, ReactNode } from "react";
import "./ElasticSlider.css";

const MAX_OVERFLOW = 34;

interface ElasticSliderProps {
  value?: number;
  defaultValue?: number;
  startingValue?: number;
  maxValue?: number;
  className?: string;
  isStepped?: boolean;
  stepSize?: number;
  leftIcon?: ReactNode;
  rightIcon?: ReactNode;
  ariaLabel: string;
  suffix?: string;
  /** BCP 47 locale for the value readout; defaults to the document/browser language. */
  locale?: string;
  disabled?: boolean;
  onChange?: (value: number) => void;
  onCommit?: (value: number) => void;
}

export default function ElasticSlider({
  value,
  defaultValue = 50,
  startingValue = 0,
  maxValue = 100,
  className = "",
  isStepped = false,
  stepSize = 1,
  leftIcon = <span aria-hidden="true">-</span>,
  rightIcon = <span aria-hidden="true">+</span>,
  ariaLabel,
  suffix = "",
  locale,
  disabled = false,
  onChange,
  onCommit,
}: ElasticSliderProps) {
  const initialValue = value ?? defaultValue;
  const [draft, setDraft] = useState(initialValue);
  const valueRef = useRef(initialValue);
  const lastCommittedRef = useRef(initialValue);
  const sliderRef = useRef<HTMLDivElement>(null);
  const [region, setRegion] = useState<"left" | "middle" | "right">("middle");
  const clientX = useMotionValue(0);
  const overflow = useMotionValue(0);
  const scale = useMotionValue(1);
  const reducedMotion = useReducedMotion();

  useEffect(() => {
    if (value === undefined) return;
    setDraft(value);
    valueRef.current = value;
    lastCommittedRef.current = value;
  }, [value]);

  const iconOpacity = useTransform(scale, [1, 1.12], [0.68, 1]);
  const trackHeight = useTransform(scale, [1, 1.12], [6, 10]);
  const trackMargin = useTransform(scale, [1, 1.12], [0, -2]);
  const trackScaleY = useTransform(overflow, [0, MAX_OVERFLOW], [1, 0.84]);
  const trackScaleX = useTransform(() => {
    const width = sliderRef.current?.getBoundingClientRect().width ?? 1;
    return 1 + overflow.get() / width;
  });
  const transformOrigin = useTransform(() => {
    const rect = sliderRef.current?.getBoundingClientRect();
    if (!rect) return "center";
    return clientX.get() < rect.left + rect.width / 2 ? "right" : "left";
  });
  const leftX = useTransform(() => region === "left" ? -overflow.get() / scale.get() : 0);
  const rightX = useTransform(() => region === "right" ? overflow.get() / scale.get() : 0);

  const precision = Math.max(decimalPlaces(startingValue), decimalPlaces(maxValue), decimalPlaces(stepSize));
  const normalize = (next: number) => {
    const safeStep = stepSize > 0 ? stepSize : 1;
    const stepped = isStepped
      ? startingValue + Math.round((next - startingValue) / safeStep) * safeStep
      : next;
    return Number(Math.min(Math.max(stepped, startingValue), maxValue).toFixed(precision));
  };

  const formatter = useMemo(() => {
    const resolved = locale || document.documentElement.lang || navigator.language || undefined;
    try { return new Intl.NumberFormat(resolved, { maximumFractionDigits: precision, useGrouping: false }); }
    catch { return new Intl.NumberFormat(undefined, { maximumFractionDigits: precision, useGrouping: false }); }
  }, [locale, precision]);
  const format = (next: number) => `${formatter.format(next)}${suffix}`;
  // Reserve the widest possible readout so the track never shifts while dragging.
  const sampleFraction = precision > 0 ? Number((startingValue + stepSize * 0.5 + 10 ** -precision).toFixed(precision)) : startingValue;
  const outputWidth = Math.max(format(startingValue).length, format(maxValue).length, format(sampleFraction).length);

  const update = (next: number) => {
    const normalized = normalize(next);
    if (normalized === valueRef.current) return;
    valueRef.current = normalized;
    setDraft(normalized);
    onChange?.(normalized);
  };

  const updateOverflow = (pointerX: number) => {
    const rect = sliderRef.current?.getBoundingClientRect();
    if (!rect) return;
    clientX.jump(pointerX);
    if (pointerX < rect.left) {
      setRegion("left");
      overflow.jump(decay(rect.left - pointerX, MAX_OVERFLOW));
    } else if (pointerX > rect.right) {
      setRegion("right");
      overflow.jump(decay(pointerX - rect.right, MAX_OVERFLOW));
    } else {
      setRegion("middle");
      overflow.jump(0);
    }
  };

  const updateFromPointer = (event: PointerEvent<HTMLDivElement>) => {
    const rect = sliderRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0) return;
    const ratio = (event.clientX - rect.left) / rect.width;
    update(startingValue + ratio * (maxValue - startingValue));
    updateOverflow(event.clientX);
  };

  const commit = () => {
    const current = valueRef.current;
    if (current !== lastCommittedRef.current) {
      lastCommittedRef.current = current;
      onCommit?.(current);
    }
  };

  const release = () => {
    commit();
    setRegion("middle");
    if (reducedMotion) overflow.jump(0);
    else animate(overflow, 0, { type: "spring", bounce: 0.28, visualDuration: 0.32 });
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (disabled) return;
    const delta = isStepped ? stepSize : (maxValue - startingValue) / 100;
    let next: number | undefined;
    if (event.key === "ArrowLeft" || event.key === "ArrowDown") next = valueRef.current - delta;
    if (event.key === "ArrowRight" || event.key === "ArrowUp") next = valueRef.current + delta;
    if (event.key === "Home") next = startingValue;
    if (event.key === "End") next = maxValue;
    if (next === undefined) return;
    event.preventDefault();
    update(next);
    queueMicrotask(commit);
  };

  const percentage = maxValue === startingValue ? 0 : ((draft - startingValue) / (maxValue - startingValue)) * 100;

  return (
    <div className={`elastic-slider ${disabled ? "elastic-slider--disabled" : ""} ${className}`.trim()} aria-disabled={disabled || undefined}>
      <motion.div
        className="elastic-slider__control"
        onHoverStart={() => reducedMotion || disabled ? scale.jump(1) : animate(scale, 1.12, { duration: 0.16, ease: "easeOut" })}
        onHoverEnd={() => reducedMotion ? scale.jump(1) : animate(scale, 1, { duration: 0.18, ease: "easeOut" })}
        style={{ opacity: iconOpacity }}
      >
        <motion.span className="elastic-slider__icon" animate={{ scale: region === "left" && !reducedMotion ? [1, 1.22, 1] : 1 }} style={{ x: leftX }} aria-hidden="true">
          {leftIcon}
        </motion.span>
        <div
          ref={sliderRef}
          className="elastic-slider__root"
          role="slider"
          tabIndex={disabled ? -1 : 0}
          aria-label={ariaLabel}
          aria-disabled={disabled || undefined}
          aria-valuemin={startingValue}
          aria-valuemax={maxValue}
          aria-valuenow={draft}
          aria-valuetext={format(draft)}
          onKeyDown={handleKeyDown}
          onPointerDown={event => {
            if (disabled || event.button !== 0) return;
            updateFromPointer(event);
            event.currentTarget.setPointerCapture(event.pointerId);
          }}
          onPointerMove={event => {
            if (event.currentTarget.hasPointerCapture(event.pointerId)) updateFromPointer(event);
          }}
          onPointerUp={event => {
            if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
            release();
          }}
          onPointerCancel={release}
          onLostPointerCapture={release}
          onBlur={commit}
        >
          <motion.div className="elastic-slider__track-wrap" style={{ height: trackHeight, marginTop: trackMargin, marginBottom: trackMargin, scaleX: trackScaleX, scaleY: trackScaleY, transformOrigin }}>
            <div className="elastic-slider__track">
              <div className="elastic-slider__range" style={{ width: `${percentage}%` }} />
            </div>
          </motion.div>
        </div>
        <motion.span className="elastic-slider__icon" animate={{ scale: region === "right" && !reducedMotion ? [1, 1.22, 1] : 1 }} style={{ x: rightX }} aria-hidden="true">
          {rightIcon}
        </motion.span>
      </motion.div>
      <output className="elastic-slider__value" aria-hidden="true" style={{ minWidth: `${outputWidth}ch` }}>{format(draft)}</output>
    </div>
  );
}

function decimalPlaces(value: number) {
  const [, decimals = ""] = String(value).split(".");
  return decimals.length;
}

function decay(value: number, max: number) {
  if (max === 0) return 0;
  const entry = value / max;
  return 2 * (1 / (1 + Math.exp(-entry)) - 0.5) * max;
}
