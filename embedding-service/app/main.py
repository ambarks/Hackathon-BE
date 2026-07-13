import logging
from typing import List, Optional

from fastapi import FastAPI
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("embedding-service")

MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2"

app = FastAPI(title="Clinical Trial Pre-Screening Embedding Service")

_model: Optional[SentenceTransformer] = None
_model_load_error: Optional[str] = None


def _load_model() -> None:
    global _model, _model_load_error
    try:
        logger.info("Loading embedding model %s (CPU-only)...", MODEL_NAME)
        _model = SentenceTransformer(MODEL_NAME, device="cpu")
        _model_load_error = None
        logger.info("Embedding model loaded successfully.")
    except Exception as exc:  # noqa: BLE001 - log and continue; /health reports the failure
        _model_load_error = str(exc)
        logger.error("Failed to load embedding model: %s", exc)


@app.on_event("startup")
def on_startup() -> None:
    _load_model()


class EmbedRequest(BaseModel):
    texts: List[str]


class EmbedResponse(BaseModel):
    embeddings: List[List[float]]
    model: str
    dimensions: int


@app.get("/health")
def health():
    return {
        "status": "ok" if _model is not None else "model_not_loaded",
        "model": MODEL_NAME,
        "error": _model_load_error,
    }


@app.post("/embed", response_model=EmbedResponse)
def embed(request: EmbedRequest):
    if _model is None:
        # Lazy retry in case the first attempt ran before model files were
        # fully available (e.g. a slow first-run download).
        _load_model()

    if _model is None or not request.texts:
        return EmbedResponse(embeddings=[[] for _ in request.texts], model=MODEL_NAME, dimensions=0)

    vectors = _model.encode(request.texts, convert_to_numpy=True)
    dimensions = int(vectors.shape[1]) if len(vectors) > 0 else 0

    return EmbedResponse(
        embeddings=[vector.tolist() for vector in vectors],
        model=MODEL_NAME,
        dimensions=dimensions,
    )
