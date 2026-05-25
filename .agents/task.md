# 任务

## 任务目标
创建一个跨多平台的崩字单字识别应用。

## 功能需求
 - 使用onnx模型预测用户提交的图片，输出top-10 label和置信度
 - 使用onnx嵌入模型计算用户提交图片的特征向量，和图片库中的图片做相似度计算，然后输出top-10图片、相似度和对应label
 - 如果用户没有图片，可以直接在上传区域手写，手写结果可以用于预测
 - 应用启动时，自动从huggingface拉取onnx模型和parquet图片数据，自动用嵌入模型计算图片的特征向量，以便后续相似度计算

## 技术栈
C# + avalonia

## 目标平台
- windows （一等支持，打包为msix）
- android （一等支持，打包为apk）
- macos
- linux

## 资源
 - 代码仓库为：git@github.com:kwadraten/kuzushi-classifier-app.git
 - UI层已有设计稿，请通过MCP链接stitch查看。
 - 图像数据集地址：https://huggingface.co/datasets/kwadraten/hi-utokyo-kuzushi
 - 模型权重地址：https://huggingface.co/kwadraten/shikiji