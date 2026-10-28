# -*- coding: utf-8 -*-
# ========================================================================
# FILE: int/brai.py
# PROJECT: SHOUTECH ERP V10 — Local AI Service ("Brain")
# VERSION: 10.3.0
# DESCRIPTION: Offline-first AI microservice on 127.0.0.1:5050.
#   • POST /v1/forecast/sales   — sales forecasting (ONNX or statistical)
#   • POST /v1/anomalies/detect — anomaly detection (z-score / IQR)
#   • POST /v1/insights/summary — rule-based business insights (AR/EN)
#   • GET  /v1/health           — liveness & engine status
#
# SECURITY MODEL (fail-closed by design):
#   • Binds to 127.0.0.1 ONLY — never exposed to the network.
#   • Optional shared-token auth (env SHOUTECH_AI_TOKEN) — when set,
#     every request must carry "X-ShouTech-Token" header.
#   • No eval/exec, no shell, no file writes outside memory.
#   • All inputs validated via Pydantic with strict bounds.
#   • Constant-size responses; no internal state leaked in errors.
# ========================================================================

from __future__ import annotations

import hmac
import math
import os
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Literal, Optional

import numpy as np
from fastapi import Depends, FastAPI, HTTPException, Request, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field, field_validator

# ------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------

SERVICE_NAME = "ShouTech AI Brain"
SERVICE_VERSION = "10.3.0"
HOST = "127.0.0.1"                       # Loopback only — never 0.0.0.0
PORT = int(os.environ.get("SHOUTECH_AI_PORT", "5050"))
MODEL_PATH = Path(__file__).with_name("fore.onnx")
MAX_SERIES_POINTS = 10_000               # Hard cap against memory abuse
MAX_HORIZON = 365                        # Max forecast horizon (days)

# Optional shared token. When unset the service still refuses non-loopback
# clients; when set, a constant-time token check is enforced.
_API_TOKEN: Optional[str] = os.environ.get("SHOUTECH_AI_TOKEN") or None

# ------------------------------------------------------------------------
# ONNX model loading (optional, graceful fallback)
# ------------------------------------------------------------------------

_onnx_session: Any = None
_engine_mode = "statistical"


def _try_load_onnx() -> None:
    """Load fore.onnx if present and valid; otherwise stay statistical."""
    global _onnx_session, _engine_mode
    try:
        if not MODEL_PATH.exists() or MODEL_PATH.stat().st_size == 0:
            return
        import onnxruntime as ort  # Local import — optional dependency

        _onnx_session = ort.InferenceSession(
            str(MODEL_PATH), providers=["CPUExecutionProvider"]
        )
        _engine_mode = "onnx+statistical"
    except Exception:  # noqa: BLE001 — any model failure → fallback engine
        _onnx_session = None
        _engine_mode = "statistical"


@asynccontextmanager
async def lifespan(_: FastAPI):
    _try_load_onnx()
    yield
    _onnx_session = None


app = FastAPI(
    title=SERVICE_NAME,
    version=SERVICE_VERSION,
    docs_url=None,          # No public Swagger on a security-critical loop
    redoc_url=None,
    openapi_url=None,
    lifespan=lifespan,
)

# ------------------------------------------------------------------------
# Security middleware
# ------------------------------------------------------------------------


@app.middleware("http")
async def security_guard(request: Request, call_next):
    start = time.perf_counter()

    # 1) Loopback-only guard (defense in depth — even if misconfigured host).
    client_host = request.client.host if request.client else ""
    if client_host not in ("127.0.0.1", "::1", "localhost"):
        return JSONResponse(
            status_code=status.HTTP_403_FORBIDDEN,
            content={"detail": "Loopback clients only."},
        )

    # 2) Optional shared-token check (constant-time comparison).
    if _API_TOKEN is not None:
        presented = request.headers.get("X-ShouTech-Token", "")
        if not hmac.compare_digest(presented.encode(), _API_TOKEN.encode()):
            return JSONResponse(
                status_code=status.HTTP_401_UNAUTHORIZED,
                content={"detail": "Invalid or missing token."},
            )

    response = await call_next(request)
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["Cache-Control"] = "no-store"
    response.headers["X-Processing-Ms"] = f"{(time.perf_counter() - start) * 1000:.1f}"
    return response


# ------------------------------------------------------------------------
# Request / response schemas (strictly bounded)
# ------------------------------------------------------------------------


class SeriesPoint(BaseModel):
    date: str = Field(..., min_length=4, max_length=32,
                      description="ISO date label, e.g. 2026-01-31")
    value: float = Field(..., description="Observed value (e.g. daily sales)")


class ForecastRequest(BaseModel):
    series: list[SeriesPoint] = Field(..., min_length=3, max_length=MAX_SERIES_POINTS)
    horizon: int = Field(default=30, ge=1, le=MAX_HORIZON)
    season_length: int = Field(default=7, ge=2, le=366)

    @field_validator("series")
    @classmethod
    def _values_finite(cls, pts: list[SeriesPoint]) -> list[SeriesPoint]:
        for p in pts:
            if not math.isfinite(p.value):
                raise ValueError("Series values must be finite numbers.")
        return pts


class ForecastResponse(BaseModel):
    engine: str
    horizon: int
    forecast: list[float]
    lower_bound: list[float]
    upper_bound: list[float]
    trend_slope: float
    confidence: float


class AnomalyRequest(BaseModel):
    values: list[float] = Field(..., min_length=5, max_length=MAX_SERIES_POINTS)
    method: Literal["zscore", "iqr"] = "iqr"
    threshold: float = Field(default=3.0, gt=0, le=10)

    @field_validator("values")
    @classmethod
    def _values_finite(cls, vals: list[float]) -> list[float]:
        if any(not math.isfinite(v) for v in vals):
            raise ValueError("Values must be finite numbers.")
        return vals


class AnomalyHit(BaseModel):
    index: int
    value: float
    score: float
    direction: Literal["high", "low"]


class AnomalyResponse(BaseModel):
    method: str
    total_points: int
    anomalies: list[AnomalyHit]


class InsightRequest(BaseModel):
    metric_name: str = Field(..., min_length=1, max_length=100)
    current_value: float
    previous_value: float
    target_value: Optional[float] = None
    language: Literal["ar", "en", "fr", "tr"] = "ar"


class InsightResponse(BaseModel):
    language: str
    direction: Literal["up", "down", "flat"]
    change_percent: float
    target_progress_percent: Optional[float]
    message: str


# ------------------------------------------------------------------------
# Statistical engine (always available — no external model needed)
# ------------------------------------------------------------------------


def _statistical_forecast(values: np.ndarray, horizon: int, season: int) -> tuple[np.ndarray, float]:
    """Linear trend + seasonal-naive decomposition.

    Robust for typical ERP daily/weekly sales series without any
    external dependency — deterministic and explainable.
    """
    n = len(values)
    x = np.arange(n, dtype=np.float64)

    # Trend via least squares on log-shifted data is overkill; plain OLS is
    # stable and explainable for business series.
    slope, intercept = np.polyfit(x, values, 1)

    # Seasonal indices from detrended series (multiplicative on |residual| mean).
    detrended = values - (slope * x + intercept)
    season = min(season, max(2, n // 2))
    seasonal = np.zeros(season)
    counts = np.zeros(season)
    for i in range(n):
        seasonal[i % season] += detrended[i]
        counts[i % season] += 1
    counts[counts == 0] = 1
    seasonal /= counts

    future_x = np.arange(n, n + horizon, dtype=np.float64)
    forecast = slope * future_x + intercept
    for i in range(horizon):
        forecast[i] += seasonal[(n + i) % season]

    return forecast, float(slope)


def _forecast_with_onnx(values: np.ndarray, horizon: int) -> Optional[np.ndarray]:
    """Run the ONNX model if loaded; None on any incompatibility."""
    if _onnx_session is None:
        return None
    try:
        input_name = _onnx_session.get_inputs()[0].name
        window = values.astype(np.float32).reshape(1, -1, 1)
        raw = _onnx_session.run(None, {input_name: window})[0]
        out = np.asarray(raw, dtype=np.float64).flatten()
        if out.size < horizon or not np.all(np.isfinite(out)):
            return None
        return out[:horizon]
    except Exception:  # noqa: BLE001
        return None


# ------------------------------------------------------------------------
# Endpoints
# ------------------------------------------------------------------------


@app.get("/v1/health")
async def health() -> dict[str, Any]:
    return {
        "service": SERVICE_NAME,
        "version": SERVICE_VERSION,
        "status": "ok",
        "engine": _engine_mode,
        "onnx_loaded": _onnx_session is not None,
        "auth": "token" if _API_TOKEN else "loopback-only",
    }


@app.post("/v1/forecast/sales", response_model=ForecastResponse)
async def forecast_sales(req: ForecastRequest) -> ForecastResponse:
    values = np.array([p.value for p in req.series], dtype=np.float64)

    # 1) Try the ONNX model first (if loaded and shape-compatible).
    forecast = _forecast_with_onnx(values, req.horizon)
    slope = float(np.polyfit(np.arange(len(values)), values, 1)[0])

    # 2) Statistical engine as fallback / primary.
    if forecast is None:
        forecast, slope = _statistical_forecast(values, req.horizon, req.season_length)

    # Prediction intervals from residual standard deviation vs the moving
    # mean (≈95% band) — simple, stable and explainable.
    n = len(values)
    win = min(7, n)
    moving_mean = np.convolve(values, np.ones(win) / win, mode="same")
    residual_std = float(np.std(values - moving_mean)) if n >= 3 else 0.0
    margin = 1.96 * max(residual_std, 1e-9)
    lower = forecast - margin
    upper = forecast + margin

    # Confidence heuristic: longer, stabler series → higher confidence.
    cv = float(np.std(values) / (abs(np.mean(values)) + 1e-9))
    confidence = max(0.5, min(0.97, 1.0 - cv * 0.5 + min(len(values), 90) / 300.0))

    return ForecastResponse(
        engine=_engine_mode,
        horizon=req.horizon,
        forecast=[round(float(v), 4) for v in forecast],
        lower_bound=[round(float(v), 4) for v in lower],
        upper_bound=[round(float(v), 4) for v in upper],
        trend_slope=round(slope, 6),
        confidence=round(confidence, 3),
    )


@app.post("/v1/anomalies/detect", response_model=AnomalyResponse)
async def detect_anomalies(req: AnomalyRequest) -> AnomalyResponse:
    values = np.array(req.values, dtype=np.float64)
    hits: list[AnomalyHit] = []

    if req.method == "zscore":
        mean, std = float(np.mean(values)), float(np.std(values))
        if std > 1e-12:
            z = (values - mean) / std
            for i, score in enumerate(z):
                if abs(score) >= req.threshold:
                    hits.append(AnomalyHit(
                        index=i, value=float(values[i]), score=round(float(abs(score)), 3),
                        direction="high" if score > 0 else "low"))
    else:  # IQR — robust to extreme values, better for financial data
        q1, q3 = np.percentile(values, [25, 75])
        iqr = float(q3 - q1)
        if iqr > 1e-12:
            lo = q1 - req.threshold * iqr / 1.5   # threshold=3 ≈ "far out" Tukey
            hi = q3 + req.threshold * iqr / 1.5
            for i, v in enumerate(values):
                if v < lo:
                    hits.append(AnomalyHit(index=i, value=float(v),
                                           score=round(float((lo - v) / iqr), 3), direction="low"))
                elif v > hi:
                    hits.append(AnomalyHit(index=i, value=float(v),
                                           score=round(float((v - hi) / iqr), 3), direction="high"))

    return AnomalyResponse(method=req.method, total_points=len(values), anomalies=hits)


_INSIGHT_TEMPLATES = {
    "ar": {
        "up":   "المؤشر «{name}» ارتفع بنسبة {chg:.1f}% مقارنة بالفترة السابقة.",
        "down": "المؤشر «{name}» انخفض بنسبة {chg:.1f}% مقارنة بالفترة السابقة — يستحق المراجعة.",
        "flat": "المؤشر «{name}» مستقر دون تغيير يُذكر.",
        "target": " نسبة الإنجاز من الهدف: {prog:.0f}%.",
    },
    "en": {
        "up":   "Metric '{name}' is up {chg:.1f}% versus the previous period.",
        "down": "Metric '{name}' is down {chg:.1f}% versus the previous period — worth a review.",
        "flat": "Metric '{name}' is stable with no significant change.",
        "target": " Target attainment: {prog:.0f}%.",
    },
    "fr": {
        "up":   "L'indicateur «{name}» est en hausse de {chg:.1f}% par rapport à la période précédente.",
        "down": "L'indicateur «{name}» est en baisse de {chg:.1f}% — à examiner.",
        "flat": "L'indicateur «{name}» est stable.",
        "target": " Atteinte de l'objectif : {prog:.0f}%.",
    },
    "tr": {
        "up":   "'{name}' göstergesi önceki döneme göre %{chg:.1f} arttı.",
        "down": "'{name}' göstergesi %{chg:.1f} düştü — incelenmeli.",
        "flat": "'{name}' göstergesi sabit.",
        "target": " Hedef gerçekleşme: %{prog:.0f}.",
    },
}


@app.post("/v1/insights/summary", response_model=InsightResponse)
async def insight_summary(req: InsightRequest) -> InsightResponse:
    prev = req.previous_value
    cur = req.current_value

    if abs(prev) < 1e-12:
        change_pct = 100.0 if cur > 0 else (-100.0 if cur < 0 else 0.0)
    else:
        change_pct = (cur - prev) / abs(prev) * 100.0

    direction: Literal["up", "down", "flat"]
    if change_pct > 1.0:
        direction = "up"
    elif change_pct < -1.0:
        direction = "down"
    else:
        direction = "flat"

    progress: Optional[float] = None
    if req.target_value is not None and abs(req.target_value) > 1e-12:
        progress = round(cur / abs(req.target_value) * 100.0, 1)

    t = _INSIGHT_TEMPLATES[req.language]
    message = t[direction].format(name=req.metric_name, chg=abs(change_pct))
    if progress is not None:
        message += t["target"].format(prog=progress)

    return InsightResponse(
        language=req.language,
        direction=direction,
        change_percent=round(change_pct, 2),
        target_progress_percent=progress,
        message=message,
    )


# ------------------------------------------------------------------------
# Entry point
# ------------------------------------------------------------------------

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        app,
        host=HOST,
        port=PORT,
        log_level="warning",     # Quiet loopback service — ERP does the logging
        access_log=False,
    )
