# InkNote

Surface Pro + Surface 펜으로 PDF에 필기하는 가벼운 Windows 앱입니다.
WinUI 3 + Win2D(화면) + PDFium(PDF 열기·저장)으로 만들었습니다.

## 지금 되는 것 (MVP)

- **PDF 열기**: 툴바 `열기`, `Ctrl+O`, 또는 파일을 창에 끌어다 놓기
- **펜**: 필압으로 굵기가 변합니다. 색 5가지, 굵기 5단계
- **형광펜**: 겹쳐 칠해도 진해지지 않고 아래 글자가 묻히지 않습니다
- **지우개**: 획 단위로 지웁니다. 툴바 지우개 외에 **펜 뒤쪽(지우개 끝)**, **펜 옆 버튼**, 마우스 오른쪽 버튼도 지우개로 동작합니다
- **손바닥 무시**: 손가락으로는 그려지지 않고 스크롤·핀치 확대만 됩니다. 펜이 화면 가까이 있는 동안에는 터치 스크롤도 잠깁니다
- **실행 취소/다시 실행**: `Ctrl+Z` / `Ctrl+Y`
- **저장**: `Ctrl+S`(덮어쓰기), `Ctrl+Shift+S`(다른 이름으로). 닫을 때 저장 안 한 필기가 있으면 물어봅니다
- 단축키: `P` 펜, `H` 형광펜, `E` 지우개

## 저장 형식

필기는 PDF 표준 **잉크 주석**(`/Subtype /Ink`)으로 저장됩니다. 원본 페이지 내용은 건드리지 않습니다.

- 외관 스트림(`/AP`)에 필압대로 굵기가 변하는 선을 넣어서 Acrobat, Edge, Chrome, Drawboard 등 다른 뷰어에서도 똑같이 보입니다.
  형광펜은 곱하기 블렌드 + 35% 투명도로 저장됩니다.
- 이 앱으로 다시 열면 필기가 다시 편집 가능한 획으로 돌아옵니다(필압 포함, 지우개·실행 취소 가능).
  다른 앱이 넣은 주석은 그대로 보이고 건드리지 않습니다.

## 내 PC에서 빌드·실행

Visual Studio 없이 .NET SDK만 있으면 됩니다(디스크 약 2GB).

```powershell
winget install Microsoft.DotNet.SDK.10
git clone <이 저장소 주소>
cd InkNote
dotnet run --project src/InkNote.App -c Release
```

`dotnet run`이 끝나면 `src/InkNote.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/InkNote.exe`를 바로 실행해도 됩니다.
다른 PC로 옮길 수 있는 폴더를 만들려면:

```powershell
dotnet publish src/InkNote.App -c Release -r win-x64 --self-contained -p:Platform=x64 -o publish
```

GitHub에 올려 두면 Actions가 테스트를 돌리고 `InkNote-win-x64` 실행 폴더를 artifact로 만들어 줍니다.

## 구조

```
src/InkNote.Core        UI와 무관한 핵심 (Linux에서도 테스트 가능)
  Pdfium/Native.cs        PDFium C API 선언
  Pdfium/PdfDocument.cs   열기, 페이지 렌더링, 필기 주석 불러오기/저장
  Ink/InkStroke.cs        획 모델, 필압→굵기, 지우개 판정
  Ink/InkDocument.cs      페이지별 획 + 실행 취소
  Ink/InkAnnotationCodec  획 ↔ PDF 잉크 주석
src/InkNote.App         WinUI 3 앱
  MainWindow              툴바, 파일, 스크롤/확대, 손바닥 무시
  PageView                한 쪽: PDF 비트맵 + 필기 그리기, 펜 입력
tests/InkNote.Core.Tests 저장→다시 열기 왕복, 회전된 페이지, 형광펜 투명도, 실행 취소
```

## 알려진 한계와 다음 단계

- **펜 지연**: WinUI 3에는 UWP의 `InkCanvas`가 없어서 펜 입력을 직접 받아 Win2D로 그립니다. 체감 지연이 아쉬우면
  다음 단계로 별도 스레드 입력(`InputPointerSource`)과 잉크 예측을 붙일 수 있습니다.
- 지우개는 획 단위만(부분 지우기 없음), 올가미 선택·이동 없음, 빈 페이지 추가 없음
- 아주 크게 확대하면(캔버스 4096px 상한) 조금 흐려질 수 있음
- 암호 걸린 PDF 미지원

## 라이선스

[MIT](LICENSE). 함께 쓰는 PDFium, Windows App SDK, Win2D 등의 라이선스는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 있고,
빌드한 실행 폴더에도 `LICENSE.txt`, `THIRD_PARTY_NOTICES.md`, `licenses\pdfium\`가 함께 들어갑니다.
