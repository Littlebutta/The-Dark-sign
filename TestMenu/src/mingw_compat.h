/* Force-included into Dear ImGui's DX12 backend: mingw-w64's d3d12.h lacks this typedef. A distinct name keeps it
   from clashing with headers that do have it. */
#include <d3d12.h>
#define PFN_D3D12_SERIALIZE_ROOT_SIGNATURE DksPfnSerializeRootSignature
typedef HRESULT(WINAPI *DksPfnSerializeRootSignature)(const D3D12_ROOT_SIGNATURE_DESC *, D3D_ROOT_SIGNATURE_VERSION,
                                                      ID3DBlob **, ID3DBlob **);
