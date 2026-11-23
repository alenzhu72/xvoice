import sys
from pathlib import Path
import numpy as np
import faiss
from librosa.filters import mel
import onnxruntime as ort
root, out = map(Path, sys.argv[1:])
mel(sr=16000,n_fft=1024,n_mels=128,fmin=30,fmax=8000,htk=True).astype("<f4").tofile(out/"mel.bin")
for path in (root/"assets/indices").glob("*.index"):
    idx=faiss.read_index(str(path))
    print(path, type(idx), idx.ntotal, flush=True)
    counts=[]
    groups=[]
    for n in range(idx.nlist):
        size=idx.invlists.list_size(n)
        if size:
            ptr=idx.invlists.get_codes(n)
            groups.append(faiss.rev_swig_ptr(ptr,size*idx.code_size).copy().view(np.float32).reshape(size,256))
            counts.append(size)
    q=np.vstack([g.mean(axis=0) for g in groups])
    with (out/path.name).open("wb") as f:
        np.array([len(counts),idx.ntotal],dtype="<i4").tofile(f)
        q.astype("<f4").tofile(f)
        np.asarray(counts,dtype="<i4").tofile(f)
        np.concatenate(groups).astype("<f4").tofile(f)
    print(path.name, len(counts),idx.ntotal,flush=True)
p=root/"assets/rmvpe/rmvpe.onnx"
session=ort.InferenceSession(str(p), providers=["CPUExecutionProvider"])
print([(i.name,i.shape) for i in session.get_inputs()],[(i.name,i.shape) for i in session.get_outputs()],flush=True)








