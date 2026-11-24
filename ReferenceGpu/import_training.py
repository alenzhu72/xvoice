"""Offline bridge for existing RVC training outputs. Application inference uses C#."""
import sys, pathlib, os, uuid, shutil, subprocess
root, weight, index, output, deps = [pathlib.Path(p).resolve() for p in sys.argv[1:]]
os.chdir(root)
sys.path[:0] = [str(deps), str(root)]
import faiss
import numpy as np
output.mkdir(parents=True, exist_ok=True)
alias="xvoice_import_"+uuid.uuid4().hex
temporary=root/"assets/weights"/(alias+".pth")
try:
    shutil.copyfile(weight,temporary)
    # The application supplies the shared encoder. This export only needs the voice generator.
    (output/"contentvec.onnx").touch()
    subprocess.run([sys.executable,"-X","utf8",str(pathlib.Path(__file__).parent/"export_model_profile.py"),str(root),str(output),str(deps),alias],check=True)
    (output/(alias+".onnx")).replace(output/"voice.onnx")
    idx=faiss.read_index(str(index))
    if idx.d != 256 or not isinstance(idx,faiss.IndexIVFFlat):
        raise ValueError("Expected a 256-D RVC v1 IVF-flat index")
    quantizer=idx.quantizer
    flat=faiss.downcast_index(quantizer)
    centers=[]; counts=[]; groups=[]
    for i in range(idx.nlist):
        n=idx.invlists.list_size(i)
        if not n: continue
        centers.append(flat.reconstruct(i))
        counts.append(n)
        ptr=idx.invlists.get_codes(i)
        groups.append(faiss.rev_swig_ptr(ptr,n*idx.code_size).copy().view(np.float32).reshape(n,256))
    with (output/"voice.ivf").open("wb") as f:
        np.array([len(counts),idx.ntotal],dtype="<i4").tofile(f)
        np.vstack(centers).astype("<f4").tofile(f)
        np.asarray(counts,dtype="<i4").tofile(f)
        np.concatenate(groups).astype("<f4").tofile(f)
    print("Model and original IVF centroids exported.",flush=True)
finally:
    temporary.unlink(missing_ok=True)
    (output/"contentvec.onnx").unlink(missing_ok=True)
