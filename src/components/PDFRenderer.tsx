import React, { useEffect, useState, useRef } from 'react';
import html2canvas from 'html2canvas';
import { PDFDownloadLink } from '@react-pdf/renderer';
import { Button } from 'antd';

interface PDFRendererProps {
  componentToRender: (
    Ref: React.MutableRefObject<any>,
    render: () => void,
    width: number,
    height: number,
  ) => JSX.Element; // The component you want to render to PNG with width and height
  pdfFileName: string;
  pdfComponent: (img: string) => JSX.Element; // Your PDF component that accepts the PNG image
  width: number;
  height: number;
}

//this is a reusable component for render pdf file needing a png image

const PDFRenderer: React.FC<PDFRendererProps> = ({
  componentToRender,
  pdfFileName,
  pdfComponent,
  width,
  height,
}) => {
  const [pngDataUrl, setPngDataUrl] = useState<string | null>(null);
  const componentRef = useRef<HTMLDivElement>(null);

  const generatePdf = () => {
    if (componentRef.current) {
      html2canvas(componentRef.current).then((canvas) => {
        const dataUrl = canvas.toDataURL('image/png');
        setPngDataUrl(dataUrl); //set address of new png file
      });
    }
  };

  return (
    <div>
      {componentToRender(componentRef, generatePdf, width, height)}
      {pngDataUrl && (
        <>
          {/* render pdf here */}
          <PDFDownloadLink document={pdfComponent(pngDataUrl)} fileName={pdfFileName}>
            {({ loading }) =>
              loading ? (
                ''
              ) : (
                <Button type='primary' className='defualt-margin '>
                  دریافت فایل
                </Button>
              )
            }
          </PDFDownloadLink>
        </>
      )}
    </div>
  );
};

export default PDFRenderer;
