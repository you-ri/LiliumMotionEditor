"""Run kimodo_gen with a cache of encoded prompts (one file per prompt).

Run inside the Kimodo environment (the Docker image or a virtual environment with
the kimodo package). Takes the same arguments as kimodo_gen.

  python kimodo_generate.py "a person walks forward" --duration 2 --constraints c.json --output out --bvh

Most of a Kimodo generation is encoding the prompt with Llama 3 8B (about 50 seconds on
the CPU, plus about 4 minutes to load it when there is no text server). Generating again
with the same prompt (after changing the keys, for example) reuses the encoded prompt, so
neither the text server nor the text model is needed. A new prompt is encoded as kimodo_gen
would (TEXT_ENCODER_MODE: api for the text server, local to load the model here) and kept.

LILIUM_TEXT_CACHE  Folder for the encoded prompts (default /cache)
"""

import hashlib
import importlib
import os
import sys

import numpy as np
import torch

CACHE = os.environ.get("LILIUM_TEXT_CACHE", "/cache")
# The encoded prompt depends on the text encoder; keep different encoders apart
ENCODER = os.environ.get("TEXT_ENCODER", "llm2vec")


class CachedTextEncoder:
    """Text encoder for Kimodo that keeps the encoded prompts in CACHE and loads the real encoder only on a miss."""

    def __init__(self):
        self.encoder = None
        self.device = "cpu"
        self.dtype = torch.float

    def to(self, device=None, dtype=None):
        if device is not None:
            self.device = device
        if dtype is not None:
            self.dtype = dtype
        return self

    def eval(self):
        return self

    def _real(self):
        if self.encoder is None:
            loader = importlib.import_module("kimodo.model.load_model")
            url = loader.get_env_var("TEXT_ENCODER_URL", loader.DEFAULT_TEXT_ENCODER_URL)
            self.encoder = loader.instantiate_from_dict(loader._select_text_encoder_conf(url))
        return self.encoder

    def __call__(self, texts):
        if isinstance(texts, str):
            texts = [texts]
        os.makedirs(CACHE, exist_ok=True)
        features = []
        for text in texts:
            key = hashlib.sha256((ENCODER + "\n" + text).encode("utf-8")).hexdigest()
            path = os.path.join(CACHE, key + ".npy")
            if os.path.exists(path):
                print("Using the cached encoding of the prompt")
                feature = np.load(path)
            else:
                tensor, lengths = self._real()([text])
                feature = tensor[0, : lengths[0]].detach().float().cpu().numpy()
                np.save(path, feature)
            features.append(feature)
        lengths = [f.shape[0] for f in features]
        padded = np.zeros((len(features), max(lengths), features[0].shape[-1]), dtype=np.float32)
        for i, feature in enumerate(features):
            padded[i, : lengths[i]] = feature
        return torch.from_numpy(padded).to(device=self.device, dtype=self.dtype), lengths


def main():
    generate = importlib.import_module("kimodo.scripts.generate")
    original = generate.load_model

    def load_with_cache(*args, **kwargs):
        kwargs.setdefault("text_encoder", CachedTextEncoder())
        return original(*args, **kwargs)

    generate.load_model = load_with_cache
    sys.argv = ["kimodo_gen"] + sys.argv[1:]
    generate.main()


if __name__ == "__main__":
    main()
