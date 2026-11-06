"""Build the native Windows package using the Python standard library only."""
from pathlib import Path
import argparse
import json
import xml.etree.ElementTree as ET
from native_portable import build_native, plan_native, update_native, update_destination, export_clean_release, export_current_release, recover_native_update

ROOT = Path(__file__).resolve().parents[1]

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--update', action='store_true', help='离线自检后原位更新已有安装；默认当前发布包，保留用户数据与未知文件')
    parser.add_argument('--recover-update', action='store_true', help='清除已确认 stale 更新锁，或按清单和哈希恢复唯一未完成回退')
    parser.add_argument('--export-clean', type=Path, nargs='?', const=Path('.'), help='导出无用户数据ZIP；省略路径时按当前版本命名并清理同目录旧版本包')
    parser.add_argument('--reviewed-install', type=Path, help='显式审核的项目编译文件路径及当前SHA256凭据，仅用于update')
    parser.add_argument('--native', action='store_true', help='兼容旧原生命令；现在默认只构建 .NET')
    parser.add_argument('--dry-run', action='store_true', help='只读列出资源与缺项')
    parser.add_argument('--model', type=Path, help='OCR 识别模型路径')
    parser.add_argument('--model-license', type=Path, help='OCR 模型许可和来源说明文件')
    args = parser.parse_args()
    if args.recover_update:
        if args.update or args.dry_run or args.export_clean or args.reviewed_install or args.model or args.model_license:
            parser.error('--recover-update 只能与 --output 配合使用')
        print(recover_native_update(ROOT,args.output or ROOT/'发布包/MabiAgent-NET验收版'))
        raise SystemExit(0)
    if args.export_clean:
        if args.update or args.dry_run:
            parser.error('--export-clean 不能与 --update/--dry-run 混用')
        directory=update_destination(ROOT,args.output)
        if args.export_clean==Path('.'):
            version=ET.parse(ROOT/'dotnet/Directory.Build.props').findtext('.//Version') or ''
            print(export_current_release(directory,version))
        else:
            print(export_clean_release(directory,args.export_clean))
        raise SystemExit(0)
    if args.reviewed_install and not args.update:
        parser.error('--reviewed-install 仅可用于 --update')
    if args.reviewed_install:
        from native_portable import _reviewed_install_receipt
        _reviewed_install_receipt(args.reviewed_install)
    options = dict(model=args.model, model_license=args.model_license)
    if args.update:
        options['reviewed_receipt'] = args.reviewed_install
    output = args.output or (ROOT / '发布包/MabiAgent-NET验收版' if args.update else ROOT / 'dist/native')
    if args.dry_run:
        if args.update:
            print(json.dumps(dict(destination=str(update_destination(ROOT, output)), mode='update', user_data_copied=False), ensure_ascii=False, indent=2))
        else:
            print(json.dumps(plan_native(ROOT, output, **options), ensure_ascii=False, indent=2))
    else:
        destination=(update_native if args.update else build_native)(ROOT, output, **options)
        print(destination)
        if args.update:
            version=ET.parse(ROOT/'dotnet/Directory.Build.props').findtext('.//Version') or ''
            print(export_current_release(destination,version))
