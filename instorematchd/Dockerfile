FROM python:3.12-alpine

# instorematchd 只用标准库，不需要 pip install
WORKDIR /app
COPY instorematchd.py test_protocol.py ./

EXPOSE 20100 20101

# --host-override 必须是客户端能访问到的地址（域名或公网 IP），
# 否则 /info 会把中继地址返回成容器内部地址，客户端连不上。
ENTRYPOINT ["python", "-u", "instorematchd.py"]
CMD ["--host-override", "example.com", "--lobby-port", "20100", "--relay-port", "20101"]
