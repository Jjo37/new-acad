;; Hank.lsp — C3D 启动时自动加载汉克面板插件
;;
;; 被 acad.lsp 调用，或手动 (load "Hank.lsp")
;; 项目副本: D:\new-acad\plugin\Hank.lsp
;;
;; 注意: C3D 2025+ 的 acad.lsp 在 C3D 安装目录下
;; 2026-08-03 重写：修复编码（原文件 UTF-8 被 GBK 误读成乱码）

(if (not *hank-plugins-loaded*)
  (progn
    (setq *hank-plugins-loaded* T)
    (command "NETLOAD" "D:/new-acad/plugin/AcBridge-v24/Civil3DMcpPlugin.dll")
    (princ "\n[汉克] 面板: HANK_SHOW")
  )
)
(princ)
