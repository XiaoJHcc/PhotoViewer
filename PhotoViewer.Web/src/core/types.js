/**
 * core/ 模块之间共用的数据形状（只有 JSDoc，无运行时代码）。
 *
 * @typedef {Object} Exif
 * @property {number|null} aperture     光圈 f 值，如 2.8
 * @property {number|null} exposure     快门秒数，如 0.004、2
 * @property {number|null} iso
 * @property {number|null} focal        实际焦距 mm
 * @property {number|null} equivFocal   等效焦距 mm
 * @property {string|null} camera
 * @property {string|null} lens
 * @property {number|null} ev           曝光补偿 EV
 * @property {string|null} wb           白平衡文字
 * @property {string|null} flash        闪光灯文字
 *
 * @typedef {Object} Features           特征库中的一条（DINO + CV），网格 32×32、按显示朝向
 * @property {Float32Array} cls         全局特征，单位向量
 * @property {Float32Array} patches     32×32 个 patch 特征，行优先，每个 PATCH_DIM 维单位向量
 * @property {Float32Array} pca         32×32×3 PCA-RGB，0–1
 * @property {Float32Array} sharpness   32×32 锐度，0–1
 * @property {{ dx: Float32Array, dy: Float32Array, mask: Uint8Array }} shake  32×32 抖动矢量场
 * @property {string} verdict           抖动判定文字
 *
 * @typedef {Object} Photo              文件夹里的一个文件
 * @property {string} id                全局唯一，= `${folderId}/${name}`
 * @property {string} folderId
 * @property {string} name              文件名含扩展名
 * @property {string} stem              去扩展名
 * @property {string} ext               小写含点，如 ".arw"
 * @property {number} size              字节
 * @property {number} modified          文件修改时间 epoch ms
 * @property {number|null} taken        EXIF 拍摄时间 epoch ms；null = 缺失
 * @property {number} tzOffset          拍摄时区偏移（分钟，东正）
 * @property {number} rating            0–5，当前内存星级
 * @property {number} width             源像素宽（存储朝向，未应用 EXIF 方向）
 * @property {number} height
 * @property {number} orientation       EXIF 方向码 1–8；缺失按 1
 * @property {{x:number,y:number,w:number,h:number}|null} focus  Sony 对焦框，显示朝向归一化（中心 x,y 与宽高）
 * @property {Exif} exif
 * @property {boolean|null} shake       抖动判定（只读库）；null = 未分析
 * @property {boolean} extracted        是否已入特征库（同指纹组一致）
 * @property {string|null} fingerprint  同次曝光共享；缺拍摄时间为 null
 * @property {{ decode?: "full"|"all", write?: boolean }} fault  故障注入：解码失败（仅 Full / 全部）、评分写回失败
 * @property {{base:number, variant:number}} scene  程序化画面：同一 base 为同一场景（连拍），variant 为该张的微小差异
 *
 * @typedef {Object} Folder
 * @property {string} id
 * @property {string} name
 * @property {Photo[]} photos           枚举顺序
 * @property {boolean} enumerable       false = 父目录不可枚举（OPEN-03 单图模式）
 *
 * @typedef {Object} Card               同名合并后的一张卡片
 * @property {string} key               = 代表文件 id
 * @property {Photo} photo              代表文件
 * @property {Photo[]} companions       被合并的隐藏伴侣（不含代表）
 * @property {string} displayName
 *
 * @typedef {{x:number,y:number,w:number,h:number}} Rect  逻辑点
 */

export const GRID = 32;
export const PATCH_DIM = 8;
export const CLS_DIM = 64;
