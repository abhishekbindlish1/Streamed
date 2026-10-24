.PHONY: run build test tidy clean

BIN := bin/streamed-backend

run:
	go run .

build:
	mkdir -p bin
	go build -o $(BIN) .

test:
	go test ./...

tidy:
	go mod tidy

clean:
	rm -rf bin
