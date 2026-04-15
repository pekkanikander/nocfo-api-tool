.PHONY: build test test-online publish publish-osx publish-linux publish-win clean

build:
	dotnet build

test:
	dotnet test tests

test-online:
	bash tests-online/test-list.sh

publish: publish-osx publish-linux publish-win

publish-osx:
	dotnet publish tools -r osx-arm64 --self-contained -o dist/osx-arm64

publish-linux:
	dotnet publish tools -r linux-x64 --self-contained -o dist/linux-x64

publish-win:
	dotnet publish tools -r win-x64 --self-contained -o dist/win-x64

clean:
	dotnet clean
	rm -rf dist
