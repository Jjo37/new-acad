# Civil 3D API 参考资料 (2026-05-27收藏)

## DXF 参考
- 通用图元组码: https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-3610039E-27D1-4E23-B6D3-7E60B22BB5BD.htm
- 组码值类型参考: https://help.autodesk.com/cloudhelp/2016/CHT/AutoCAD-DXF/files/GUID-2553CF98-44F6-4828-82DD-FE3BC7448113.htm
- 符号表组码: https://help.autodesk.com/cloudhelp/2021/CHS/AutoCAD-DXF/files/GUID-5AB9300F-F0AC-4ADE-89EA-A9D1D152D8B8.htm

## API 开发者指南
- C3D 2025 Developer's Guide: https://help.autodesk.com/view/CIV3D/2025/ENU/?guid=GUID-DA303320-B66D-4F4F-A4F4-9FBBEC0754E0
- C3D 2024 Developer's Guide: https://help.autodesk.com/cloudhelp/2024/ENU/Civil3D-DevGuide/files/GUID-D6E4AE16-31DD-42E4-86F4-29B4AC93C453.htm
- C3D 2018 Developer's Guide: https://help.autodesk.com/view/CIV3D/2018/ENU/?guid=GUID-275A6271-7758-4C14-9703-989B1B007E3E

## COM / .NET API
- COM API 章节 (开发者指南内): C3D安装目录下 CHM 文件
  `C:\Program Files\Common Files\Autodesk Shared\Civil Engineering <版本号>\`
- C3D .NET API 参考 (2025): https://help.autodesk.com/view/CIV3D/2025/ENU/?guid=89ffd413-aada-d770-e322-89dfa7b99369
- C3D API 总览: https://aps.autodesk.com/developer/overview/civil-3d

## 第15轮 DS 讨论结论
- 几何数据: entnext 遍历子图元 (Surface → AcDbFace/AcDb3dPolyline, Alignment → LINE/ARC/SPIRAL, Profile → LINE)
- 统计/桩号: COM 死胡同 → Python comtypes 外部进程
- 双轨方案: accoreconsole entnext + Python COM bridge
